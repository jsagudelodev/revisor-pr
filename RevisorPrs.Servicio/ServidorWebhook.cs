using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RevisorPrs.Servicio;

/// <summary>
/// Recibe los avisos de Bitbucket y encola el pull request para revisarlo enseguida (C2).
/// </summary>
/// <remarks>
/// Con solo sondeo, quien empuja un commit espera hasta <c>Sondeo.IntervaloMinutos</c>
/// —cinco minutos por defecto— a que le llegue la revisión, y para entonces ya cambió de
/// tarea. Con el aviso, la revisión arranca en segundos.
///
/// El sondeo NO se sustituye: sigue como red de seguridad para los avisos que se pierdan,
/// para los pull requests abiertos antes de configurar el webhook y para cuando la cola
/// se llene.
///
/// Sobre la respuesta HTTP: se contesta en cuanto el pull request está encolado, sin
/// esperar a la revisión. Bitbucket espera una respuesta rápida y una revisión tarda lo
/// que tarde el modelo.
/// </remarks>
public sealed class ServidorWebhook : BackgroundService
{
    /// <summary>Tamaño máximo del cuerpo que se acepta.</summary>
    private const int LimiteCuerpoBytes = 1_048_576;

    /// <summary>Tamaño máximo de las cabeceras.</summary>
    private const int LimiteCabeceraBytes = 16_384;

    private readonly ILogger<ServidorWebhook> _logger;
    private readonly ConfiguracionWebhook _configuracion;
    private readonly ColaDeRevisiones _cola;
    private readonly TraductorEventoPr _traductor;
    private readonly EstadoServicio? _estado;

    private TcpListener? _escucha;

    public ServidorWebhook(
        ILogger<ServidorWebhook> logger,
        ConfiguracionWebhook configuracion,
        ColaDeRevisiones cola,
        TraductorEventoPr traductor,
        EstadoServicio? estado = null)
    {
        _logger = logger;
        _configuracion = configuracion ?? throw new ArgumentNullException(nameof(configuracion));
        _cola = cola ?? throw new ArgumentNullException(nameof(cola));
        _traductor = traductor ?? throw new ArgumentNullException(nameof(traductor));
        _estado = estado;
    }

    /// <summary>Puerto realmente en uso. Con 0 lo elige el sistema (útil en tests).</summary>
    public int PuertoActivo { get; private set; }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_configuracion.Habilitado)
        {
            _logger.LogInformation(
                "Webhook deshabilitado (Webhook.Habilitado = false). El sondeo sigue funcionando.");
            return base.StartAsync(cancellationToken);
        }

        ConfiguracionWebhook.ValidarConfiguracion(_configuracion);

        var direccion = IPAddress.Parse(_configuracion.Direccion.Trim());
        var escucha = new TcpListener(direccion, _configuracion.Puerto);

        try
        {
            escucha.Start();
        }
        catch (SocketException ex)
        {
            escucha.Stop();
            throw new InvalidOperationException(
                $"No se pudo abrir el webhook en {direccion}:{_configuracion.Puerto} ({ex.Message}). Comprueba que el puerto esté libre.",
                ex);
        }

        _escucha = escucha;
        PuertoActivo = ((IPEndPoint)escucha.LocalEndpoint).Port;

        _logger.LogInformation(
            "Webhook escuchando en {Direccion}:{Puerto}{Ruta}.",
            direccion, PuertoActivo, _configuracion.Ruta);

        if (!IPAddress.IsLoopback(direccion))
        {
            // Merece un aviso explícito: el endpoint queda accesible desde la red.
            _logger.LogWarning(
                "El webhook escucha en una interfaz no loopback ({Direccion}). Ponlo detrás de un proxy inverso con TLS.",
                direccion);
        }

        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _escucha?.Stop();
        _escucha = null;
        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var escucha = _escucha;
        if (escucha is null)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            TcpClient cliente;
            try
            {
                cliente = await escucha.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }

            _ = AtenderAsync(cliente, stoppingToken);
        }
    }

    private async Task AtenderAsync(TcpClient cliente, CancellationToken cancelacion)
    {
        using (cliente)
        {
            try
            {
                var flujo = cliente.GetStream();
                var peticion = await LeerPeticionAsync(flujo, cancelacion).ConfigureAwait(false);

                if (peticion is null)
                {
                    await ResponderAsync(flujo, 400, "Bad Request", "peticion no valida", cancelacion);
                    return;
                }

                if (!peticion.Metodo.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    await ResponderAsync(flujo, 405, "Method Not Allowed", "solo se admite POST", cancelacion);
                    return;
                }

                if (!peticion.Ruta.Equals(_configuracion.Ruta, StringComparison.Ordinal))
                {
                    await ResponderAsync(flujo, 404, "Not Found", "recurso no encontrado", cancelacion);
                    return;
                }

                if (!Autenticado(peticion))
                {
                    // Sin detalles del motivo: quien no está autenticado tampoco necesita
                    // saber si falló la firma o el token.
                    _logger.LogWarning("Aviso de webhook rechazado por autenticación inválida.");
                    _estado?.RegistrarError("webhook: aviso rechazado por autenticación inválida");
                    await ResponderAsync(flujo, 401, "Unauthorized", "no autorizado", cancelacion);
                    return;
                }

                var pr = InterpretarEvento(peticion.Cuerpo);
                if (pr is null)
                {
                    // 200 a propósito: el aviso llegó bien, simplemente no era un evento
                    // de pull request que nos interese. Un error haría que Bitbucket lo
                    // reintentara sin necesidad.
                    await ResponderAsync(flujo, 200, "OK", "evento ignorado", cancelacion);
                    return;
                }

                if (_cola.Encolar(pr))
                {
                    _logger.LogInformation(
                        "Aviso recibido para {Repositorio}#{Numero}. Encolado para revisión inmediata.",
                        pr.Repositorio, pr.Numero);
                    await ResponderAsync(flujo, 202, "Accepted", "encolado", cancelacion);
                }
                else
                {
                    // La cola llena no es un error para Bitbucket: el sondeo lo recogerá.
                    _logger.LogWarning(
                        "Cola de revisiones llena ({Capacidad}). {Repositorio}#{Numero} queda para el sondeo.",
                        ColaDeRevisiones.Capacidad, pr.Repositorio, pr.Numero);
                    await ResponderAsync(flujo, 202, "Accepted", "en cola de sondeo", cancelacion);
                }
            }
            catch (OperationCanceledException)
            {
                // El servicio se está parando a mitad de una respuesta.
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Error atendiendo un aviso de webhook: {Mensaje}", ex.Message);
            }
        }
    }

    /// <summary>
    /// Comprueba el secreto. Basta con que cuadre la firma HMAC del cuerpo o el token
    /// en la cabecera Authorization.
    /// </summary>
    /// <remarks>
    /// Las comparaciones son de tiempo constante: comparar secretos con igualdad normal
    /// filtra, por el tiempo de respuesta, cuántos caracteres iniciales se acertaron.
    /// </remarks>
    internal bool Autenticado(PeticionWebhook peticion)
    {
        string secreto = _configuracion.Secreto ?? string.Empty;
        if (secreto.Length == 0)
        {
            return false;
        }

        if (peticion.Cabeceras.TryGetValue(_configuracion.CabeceraFirma.ToLowerInvariant(), out string? firma)
            && FirmaValida(firma, peticion.CuerpoCrudo, secreto))
        {
            return true;
        }

        if (peticion.Cabeceras.TryGetValue("authorization", out string? autorizacion))
        {
            const string prefijo = "Bearer ";
            if (autorizacion.StartsWith(prefijo, StringComparison.OrdinalIgnoreCase))
            {
                string token = autorizacion.Substring(prefijo.Length).Trim();
                if (IgualesEnTiempoConstante(token, secreto))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool FirmaValida(string? firmaRecibida, byte[] cuerpo, string secreto)
    {
        if (string.IsNullOrWhiteSpace(firmaRecibida))
        {
            return false;
        }

        // Se admite tanto "sha256=<hex>" como el hex a secas.
        string recibida = firmaRecibida.Trim();
        int igual = recibida.IndexOf('=');
        if (igual >= 0 && recibida.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            recibida = recibida.Substring(igual + 1);
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secreto));
        string esperada = Convert.ToHexString(hmac.ComputeHash(cuerpo)).ToLowerInvariant();

        return IgualesEnTiempoConstante(recibida.ToLowerInvariant(), esperada);
    }

    private static bool IgualesEnTiempoConstante(string a, string b)
    {
        byte[] baA = Encoding.UTF8.GetBytes(a);
        byte[] baB = Encoding.UTF8.GetBytes(b);
        return baA.Length == baB.Length && CryptographicOperations.FixedTimeEquals(baA, baB);
    }

    /// <summary>
    /// Saca el pull request del cuerpo del aviso. Devuelve null si el evento no trae uno
    /// que podamos revisar.
    /// </summary>
    internal PullRequest? InterpretarEvento(string cuerpo)
    {
        if (string.IsNullOrWhiteSpace(cuerpo))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(cuerpo);
            var raiz = doc.RootElement;

            if (!raiz.TryGetProperty("pullrequest", out var pullrequest))
            {
                return null;
            }

            // El repositorio del aviso es mas fiable que deducirlo del enlace del PR.
            string? repositorio = null;
            if (raiz.TryGetProperty("repository", out var repo)
                && repo.TryGetProperty("full_name", out var nombre)
                && nombre.ValueKind == JsonValueKind.String)
            {
                repositorio = nombre.GetString();
            }

            var evento = _traductor.Traducir(pullrequest);
            if (evento is null)
            {
                return null;
            }

            return new PullRequest(
                repositorio ?? evento.Repositorio,
                evento.Numero,
                evento.Commit,
                evento.Titulo,
                evento.Descripcion,
                evento.Rama);
        }
        catch (JsonException)
        {
            _logger.LogWarning("El cuerpo del aviso de webhook no es JSON válido. Se ignora.");
            return null;
        }
    }

    /// <summary>Petición HTTP mínima, ya troceada.</summary>
    internal sealed record PeticionWebhook(
        string Metodo,
        string Ruta,
        IReadOnlyDictionary<string, string> Cabeceras,
        string Cuerpo,
        byte[] CuerpoCrudo);

    private static async Task<PeticionWebhook?> LeerPeticionAsync(NetworkStream flujo, CancellationToken cancelacion)
    {
        var acumulado = new List<byte>(4096);
        var buffer = new byte[4096];
        int finCabeceras = -1;

        while (acumulado.Count < LimiteCabeceraBytes)
        {
            int leidos = await flujo.ReadAsync(buffer.AsMemory(), cancelacion).ConfigureAwait(false);
            if (leidos <= 0)
            {
                break;
            }

            for (int i = 0; i < leidos; i++)
            {
                acumulado.Add(buffer[i]);
            }

            finCabeceras = BuscarFinCabeceras(acumulado);
            if (finCabeceras >= 0)
            {
                break;
            }
        }

        if (finCabeceras < 0)
        {
            return null;
        }

        string textoCabeceras = Encoding.ASCII.GetString(acumulado.ToArray(), 0, finCabeceras);
        string[] lineas = textoCabeceras.Split('\n');
        string[] partes = lineas[0].TrimEnd('\r').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (partes.Length < 2)
        {
            return null;
        }

        string ruta = partes[1];
        int consulta = ruta.IndexOf('?');
        if (consulta >= 0)
        {
            ruta = ruta.Substring(0, consulta);
        }

        var cabeceras = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lineas.Length; i++)
        {
            string linea = lineas[i].TrimEnd('\r');
            int dosPuntos = linea.IndexOf(':');
            if (dosPuntos > 0)
            {
                cabeceras[linea.Substring(0, dosPuntos).Trim().ToLowerInvariant()] =
                    linea.Substring(dosPuntos + 1).Trim();
            }
        }

        int longitud = 0;
        if (cabeceras.TryGetValue("content-length", out string? valorLongitud))
        {
            int.TryParse(valorLongitud, NumberStyles.Integer, CultureInfo.InvariantCulture, out longitud);
        }

        if (longitud > LimiteCuerpoBytes)
        {
            return null;
        }

        var cuerpo = new List<byte>(longitud);
        int inicioCuerpo = finCabeceras + 4;
        for (int i = inicioCuerpo; i < acumulado.Count && cuerpo.Count < longitud; i++)
        {
            cuerpo.Add(acumulado[i]);
        }

        while (cuerpo.Count < longitud)
        {
            int leidos = await flujo.ReadAsync(buffer.AsMemory(), cancelacion).ConfigureAwait(false);
            if (leidos <= 0)
            {
                break;
            }

            for (int i = 0; i < leidos && cuerpo.Count < longitud; i++)
            {
                cuerpo.Add(buffer[i]);
            }
        }

        byte[] crudo = cuerpo.ToArray();
        return new PeticionWebhook(partes[0], ruta, cabeceras, Encoding.UTF8.GetString(crudo), crudo);
    }

    private static int BuscarFinCabeceras(List<byte> datos)
    {
        for (int i = 0; i + 3 < datos.Count; i++)
        {
            if (datos[i] == (byte)'\r' && datos[i + 1] == (byte)'\n'
                && datos[i + 2] == (byte)'\r' && datos[i + 3] == (byte)'\n')
            {
                return i;
            }
        }
        return -1;
    }

    private static async Task ResponderAsync(
        NetworkStream flujo, int codigo, string motivo, string mensaje, CancellationToken cancelacion)
    {
        byte[] cuerpo = Encoding.UTF8.GetBytes($"{{\"resultado\":\"{mensaje}\"}}");

        var cabecera = new StringBuilder();
        cabecera.Append("HTTP/1.1 ").Append(codigo.ToString(CultureInfo.InvariantCulture))
            .Append(' ').Append(motivo).Append("\r\n");
        cabecera.Append("Content-Type: application/json; charset=utf-8\r\n");
        cabecera.Append("Content-Length: ").Append(cuerpo.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        cabecera.Append("Connection: close\r\n\r\n");

        await flujo.WriteAsync(Encoding.ASCII.GetBytes(cabecera.ToString()).AsMemory(), cancelacion).ConfigureAwait(false);
        await flujo.WriteAsync(cuerpo.AsMemory(), cancelacion).ConfigureAwait(false);
        await flujo.FlushAsync(cancelacion).ConfigureAwait(false);
    }
}
