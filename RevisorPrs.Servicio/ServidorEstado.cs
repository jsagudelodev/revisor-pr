using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RevisorPrs.Servicio;

/// <summary>
/// Endpoint local de estado (RV.20). Expone por HTTP en <c>GET /estado</c> una
/// fotografía JSON del sondeo: última vuelta, próxima vuelta, contadores y errores.
///
/// Dos reglas no negociables, ambas cubiertas por tests:
/// 1. Solo escucha en loopback. La dirección se valida al arrancar
///    (<see cref="ValidarDireccion"/>) y, además, se rechaza cualquier prefijo que no
///    sea de loopback antes de abrir el socket. Un <c>Estado.Direccion = 0.0.0.0</c>
///    o una IP de red tira del servicio en el arranque en lugar de publicar el estado.
/// 2. Nunca devuelve secretos. El cuerpo se pasa por <see cref="SaneadorSecretos"/>
///    antes de escribirse en el socket, así que una clave de API o una contraseña de
///    aplicación que se hubiera colado en un mensaje de error sale enmascarada.
///
/// Está implementado sobre <see cref="TcpListener"/> en vez de <c>HttpListener</c> a
/// propósito: en Windows <c>HttpListener</c> exige una reserva de URL (administrador)
/// incluso para loopback, y eso haría el endpoint imposible de probar en CI.
/// </summary>
public sealed class ServidorEstado : BackgroundService
{
    /// <summary>
    /// Ruta única que responde. Todo lo demás devuelve 404.
    /// </summary>
    public const string RutaEstado = "/estado";

    /// <summary>
    /// Tamaño máximo de la cabecera HTTP que aceptamos. Más allá se descarta la
    /// conexión: el endpoint no es un servidor general.
    /// </summary>
    private const int LimiteCabeceraBytes = 8192;

    private readonly ILogger<ServidorEstado> _logger;
    private readonly ConfiguracionEstado _configuracion;
    private readonly EstadoServicio _estado;
    private readonly SaneadorSecretos _saneador;

    private TcpListener? _escucha;

    public ServidorEstado(
        ILogger<ServidorEstado> logger,
        ConfiguracionEstado configuracion,
        EstadoServicio estado,
        SaneadorSecretos? saneador = null)
    {
        _logger = logger;
        _configuracion = configuracion ?? throw new ArgumentNullException(nameof(configuracion));
        _estado = estado ?? throw new ArgumentNullException(nameof(estado));
        _saneador = saneador ?? SaneadorSecretos.Ninguno;
    }

    /// <summary>
    /// Dirección real en la que se está escuchando. Siempre es loopback.
    /// </summary>
    public IPAddress? DireccionActiva { get; private set; }

    /// <summary>
    /// Puerto real en uso. Coincide con <c>Estado.Puerto</c>, salvo cuando se pidió 0,
    /// en cuyo caso lo asigna el sistema operativo al abrir el socket.
    /// </summary>
    public int PuertoActivo { get; private set; }

    /// <summary>
    /// URL base de la última escucha abierta, útil para logs y tests.
    /// </summary>
    public string UrlBase => $"http://{DireccionActiva?.ToString() ?? "sin-escucha"}:{PuertoActivo.ToString(CultureInfo.InvariantCulture)}/";

    /// <summary>
    /// Comprueba que una dirección de escucha es loopback. Lanza
    /// <see cref="InvalidOperationException"/> con mensaje accionable en caso contrario.
    /// </summary>
    public static void ValidarDireccion(string? direccion)
    {
        if (string.IsNullOrWhiteSpace(direccion))
        {
            throw new InvalidOperationException(
                "Estado.Direccion está vacía. Usa una dirección de loopback: 127.0.0.1, ::1 o localhost.");
        }

        string texto = direccion.Trim();

        if (texto.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!IPAddress.TryParse(texto, out IPAddress? ip))
        {
            throw new InvalidOperationException(
                $"Estado.Direccion = '{direccion}' no es una dirección IP válida. El endpoint /estado solo puede escucharse en loopback (127.0.0.1, ::1 o localhost).");
        }

        if (!EsLoopback(ip))
        {
            throw new InvalidOperationException(
                $"Estado.Direccion = '{direccion}' no es una dirección de loopback. El endpoint /estado nunca debe exponerse en una interfaz pública: usa 127.0.0.1, ::1 o localhost.");
        }
    }

    /// <summary>
    /// Convierte una dirección configurada en la <see cref="IPAddress"/> de bind.
    /// Rechaza de nuevo lo que no sea loopback: es la última barrera antes del socket.
    /// </summary>
    public static IPAddress ResolverDireccionDeEscucha(string? direccion)
    {
        ValidarDireccion(direccion);

        string texto = (direccion ?? string.Empty).Trim();

        if (texto.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        IPAddress ip = IPAddress.Parse(texto);
        if (!EsLoopback(ip))
        {
            throw new InvalidOperationException(
                $"Estado.Direccion = '{direccion}' no es una dirección de loopback.");
        }

        return ip;
    }

    /// <summary>
    /// Abre el socket ANTES de devolver, de modo que al terminar el arranque el
    /// endpoint ya está aceptando conexiones y <see cref="PuertoActivo"/> es consultable.
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_configuracion.Habilitado)
        {
            _logger.LogInformation("Endpoint de estado deshabilitado (Estado.Habilitado = false). No se abre ningún puerto.");
            return base.StartAsync(cancellationToken);
        }

        IPAddress direccion = ResolverDireccionDeEscucha(_configuracion.Direccion);

        if (_configuracion.Puerto < 0 || _configuracion.Puerto > 65_535)
        {
            throw new InvalidOperationException(
                $"Estado.Puerto está fuera de rango (valor recibido: {_configuracion.Puerto.ToString(CultureInfo.InvariantCulture)}). Usa un puerto entre 1 y 65535, u 0 para que se elija uno libre.");
        }

        TcpListener escucha = new(direccion, _configuracion.Puerto);
        try
        {
            escucha.Start();
        }
        catch (SocketException ex)
        {
            escucha.Stop();
            throw new InvalidOperationException(
                $"No se pudo abrir el endpoint de estado en {direccion}:{_configuracion.Puerto.ToString(CultureInfo.InvariantCulture)} ({ex.Message}). Comprueba que el puerto esté libre.",
                ex);
        }

        _escucha = escucha;
        DireccionActiva = direccion;
        PuertoActivo = ((IPEndPoint)escucha.LocalEndpoint).Port;

        _logger.LogInformation(
            "Endpoint de estado escuchando en {Url} (solo loopback).",
            UrlBase + RutaEstado.TrimStart('/'));

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
        TcpListener? escucha = _escucha;
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
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            // Cada conexión se atiende sin bloquear la siguiente: el endpoint responde
            // en microsegundos y un cliente lento no debe dejar el sondeo sin estado.
            _ = AtenderConexionAsync(cliente, stoppingToken);
        }
    }

    private async Task AtenderConexionAsync(TcpClient cliente, CancellationToken cancelacion)
    {
        using (cliente)
        {
            try
            {
                NetworkStream flujo = cliente.GetStream();
                string cabecera = await LeerCabeceraAsync(flujo, cancelacion).ConfigureAwait(false);

                if (cabecera.Length == 0)
                {
                    return;
                }

                (string metodo, string ruta) = InterpretarPeticion(cabecera);

                if (metodo.Length == 0)
                {
                    await EscribirRespuestaAsync(flujo, 400, "Bad Request", "{\"error\":\"petición HTTP no válida\"}", cancelacion)
                        .ConfigureAwait(false);
                    return;
                }

                if (!metodo.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                    !metodo.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    await EscribirRespuestaAsync(flujo, 405, "Method Not Allowed", "{\"error\":\"solo se admite GET\"}", cancelacion)
                        .ConfigureAwait(false);
                    return;
                }

                if (!string.Equals(ruta, RutaEstado, StringComparison.OrdinalIgnoreCase))
                {
                    await EscribirRespuestaAsync(flujo, 404, "Not Found", "{\"error\":\"recurso no encontrado\"}", cancelacion)
                        .ConfigureAwait(false);
                    return;
                }

                string cuerpo = ConstruirCuerpo();
                await EscribirRespuestaAsync(flujo, 200, "OK", cuerpo, cancelacion).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Caida del servicio a mitad de una respuesta: nada que registrar.
            }
            catch (Exception ex)
            {
                _estado.RegistrarError($"error atendiendo /estado: {ex.Message}");
                _logger.LogWarning(
                    "Error atendiendo una petición del endpoint de estado: {Mensaje}",
                    _saneador.Sanear(ex.Message));
            }
        }
    }

    /// <summary>
    /// Serializa el estado y lo sanea de secretos. El enmascarado se aplica al cuerpo
    /// completo, no solo a los mensajes de error, para que ninguna vía inesperada
    /// (un nombre de repositorio, una URL) publique una credencial.
    /// </summary>
    public string ConstruirCuerpo()
    {
        InstanteEstado instante = _estado.Capturar();

        var errores = new List<object?>(instante.UltimosErrores.Count);
        foreach (ErrorRegistrado error in instante.UltimosErrores)
        {
            errores.Add(new
            {
                utc = error.Utc,
                mensaje = _saneador.Sanear(error.Mensaje),
            });
        }

        var respuesta = new
        {
            servicio = "revisor-prs",
            escucha = new
            {
                direccion = DireccionActiva?.ToString() ?? string.Empty,
                puerto = PuertoActivo,
                publica = false,
            },
            ultimaVueltaUtc = instante.UltimaVueltaUtc,
            proximaVueltaUtc = instante.ProximaVueltaUtc,
            prs = new
            {
                revisadosUltimaVuelta = instante.RevisadosUltimaVuelta,
                omitidosUltimaVuelta = instante.OmitidosUltimaVuelta,
                fallidosUltimaVuelta = instante.FallidosUltimaVuelta,
                revisadosAcumulados = instante.RevisadosAcumulados,
                fallidosAcumulados = instante.FallidosAcumulados,
            },
            ultimosErrores = errores,
        };

        string json = JsonSerializer.Serialize(respuesta);
        return _saneador.Sanear(json);
    }

    private static bool EsLoopback(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        // Rango 127.0.0.0/8 completo: cualquier 127.x.x.x es loopback, no solo .1.
        byte[] bytes = ip.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 127;
    }

    private static async Task<string> LeerCabeceraAsync(NetworkStream flujo, CancellationToken cancelacion)
    {
        var acopiador = new StringBuilder();
        var buffer = new byte[1024];

        while (acopiador.Length < LimiteCabeceraBytes)
        {
            int leidos = await flujo.ReadAsync(buffer.AsMemory(0, buffer.Length), cancelacion).ConfigureAwait(false);
            if (leidos <= 0)
            {
                break;
            }

            acopiador.Append(Encoding.ASCII.GetString(buffer, 0, leidos));

            if (acopiador.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        return acopiador.ToString();
    }

    private static (string Metodo, string Ruta) InterpretarPeticion(string cabecera)
    {
        string lineaSolicitada = string.Empty;
        foreach (string linea in cabecera.Split('\n'))
        {
            string recortada = linea.TrimEnd('\r');
            if (recortada.Length == 0)
            {
                break;
            }

            lineaSolicitada = recortada;
            break;
        }

        string[] partes = lineaSolicitada.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (partes.Length < 2)
        {
            return (string.Empty, string.Empty);
        }

        string ruta = partes[1];
        int inicioConsulta = ruta.IndexOf('?');
        if (inicioConsulta >= 0)
        {
            ruta = ruta.Substring(0, inicioConsulta);
        }

        return (partes[0], ruta);
    }

    private static async Task EscribirRespuestaAsync(
        NetworkStream flujo,
        int codigo,
        string motivo,
        string cuerpo,
        CancellationToken cancelacion)
    {
        byte[] cuerpoBytes = Encoding.UTF8.GetBytes(cuerpo);

        var cabecera = new StringBuilder();
        cabecera.Append("HTTP/1.1 ")
            .Append(codigo.ToString(CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(motivo)
            .Append("\r\n");
        cabecera.Append("Content-Type: application/json; charset=utf-8\r\n");
        cabecera.Append("Content-Length: ")
            .Append(cuerpoBytes.Length.ToString(CultureInfo.InvariantCulture))
            .Append("\r\n");
        cabecera.Append("Cache-Control: no-store\r\n");
        cabecera.Append("Connection: close\r\n");
        cabecera.Append("\r\n");

        byte[] cabeceraBytes = Encoding.ASCII.GetBytes(cabecera.ToString());
        await flujo.WriteAsync(cabeceraBytes.AsMemory(), cancelacion).ConfigureAwait(false);
        await flujo.WriteAsync(cuerpoBytes.AsMemory(), cancelacion).ConfigureAwait(false);
        await flujo.FlushAsync(cancelacion).ConfigureAwait(false);
    }
}