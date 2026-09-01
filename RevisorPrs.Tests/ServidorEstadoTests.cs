using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using RevisorPrs.Servicio;
using Xunit;

namespace RevisorPrs.Tests;

/// <summary>
/// Pruebas de cierre del endpoint local de estado (RV.20).
///
/// Cubren las dos reglas no negociables del endpoint:
/// (a) solo escucha en loopback, nunca en una interfaz pública;
/// (b) ningún secreto configurado sale en claro en la respuesta HTTP.
/// </summary>
public class ServidorEstadoTests
{
    private const string ClaveLlm = "sk-LLM-SUPERSECRETA-0123456789";
    private const string ClaveBitbucket = "bb-clave-aplicacion-XYZ987";
    private const string UsuarioBitbucket = "usuario-bitbucket-pruebas";

    private static SaneadorSecretos SaneadorDePrueba() =>
        new SaneadorSecretos(new string?[] { ClaveLlm, ClaveBitbucket, UsuarioBitbucket });

    private static EstadoServicio NuevoEstado() =>
        new EstadoServicio(() => new DateTimeOffset(2025, 1, 15, 10, 0, 0, TimeSpan.Zero));

    private static ServidorEstado NuevoServidor(
        ConfiguracionEstado configuracion,
        EstadoServicio estado,
        SaneadorSecretos? saneador = null) =>
        new ServidorEstado(
            NullLogger<ServidorEstado>.Instance,
            configuracion,
            estado,
            saneador ?? SaneadorDePrueba());

    // ─── (a) La dirección de escucha es loopback, nunca una interfaz pública ───

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.53")]
    [InlineData("::1")]
    [InlineData("localhost")]
    [InlineData("  127.0.0.1  ")]
    public void ValidarDireccion_AceptaSoloLoopback(string direccion)
    {
        ServidorEstado.ValidarDireccion(direccion);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("192.168.1.25")]
    [InlineData("10.0.0.7")]
    [InlineData("172.16.3.9")]
    [InlineData("8.8.8.8")]
    public void ValidarDireccion_RechazaInterfazPublica(string direccion)
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ServidorEstado.ValidarDireccion(direccion));

        Assert.Contains("loopback", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(direccion, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidarDireccion_RechazaDireccionVacia(string? direccion)
    {
        Assert.Throws<InvalidOperationException>(() => ServidorEstado.ValidarDireccion(direccion));
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("999.999.999.999")]
    public void ValidarDireccion_RechazaTextoQueNoEsDireccion(string direccion)
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ServidorEstado.ValidarDireccion(direccion));

        Assert.Contains("no es una dirección IP válida", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("localhost")]
    public void ResolverDireccionDeEscucha_DevuelveSiempreLoopback(string direccion)
    {
        IPAddress resuelta = ServidorEstado.ResolverDireccionDeEscucha(direccion);

        Assert.True(IPAddress.IsLoopback(resuelta),
            $"La dirección resuelta {resuelta} no es de loopback.");
    }

    [Fact]
    public void ResolverDireccionDeEscucha_Localhost_SeResuelveAIPv4Loopback()
    {
        Assert.Equal(IPAddress.Loopback, ServidorEstado.ResolverDireccionDeEscucha("localhost"));
    }

    [Fact]
    public void ResolverDireccionDeEscucha_InterfazPublica_LanzaAntesDeAbrirSocket()
    {
        Assert.Throws<InvalidOperationException>(
            () => ServidorEstado.ResolverDireccionDeEscucha("0.0.0.0"));
    }

    [Fact]
    public void ValidarConfiguracion_DireccionPublica_FallaAlArrancar()
    {
        ConfiguracionEstado configuracion = new() { Direccion = "192.168.1.25", Puerto = 8787 };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ConfiguracionEstado.ValidarConfiguracion(configuracion));

        Assert.Contains("loopback", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65_536)]
    [InlineData(70_000)]
    public void ValidarConfiguracion_PuertoFueraDeRango_FallaAlArrancar(int puerto)
    {
        ConfiguracionEstado configuracion = new() { Direccion = "127.0.0.1", Puerto = puerto };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ConfiguracionEstado.ValidarConfiguracion(configuracion));

        Assert.Contains("fuera de rango", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidarConfiguracion_Nula_DaInstruccionesConcretas()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ConfiguracionEstado.ValidarConfiguracion(null));

        Assert.Contains("Estado", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_DireccionPublica_NoAbrePuerto()
    {
        ConfiguracionEstado configuracion = new() { Direccion = "0.0.0.0", Puerto = 0 };
        ServidorEstado servidor = NuevoServidor(configuracion, NuevoEstado());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => servidor.StartAsync(CancellationToken.None));

        Assert.Null(servidor.DireccionActiva);
        Assert.Equal(0, servidor.PuertoActivo);
    }

    [Fact]
    public async Task StartAsync_Loopback_EscuchaSoloEnLoopback()
    {
        ConfiguracionEstado configuracion = new() { Direccion = "127.0.0.1", Puerto = 0 };
        ServidorEstado servidor = NuevoServidor(configuracion, NuevoEstado());

        await servidor.StartAsync(CancellationToken.None);
        try
        {
            Assert.NotNull(servidor.DireccionActiva);
            Assert.True(IPAddress.IsLoopback(servidor.DireccionActiva!),
                $"El endpoint quedó escuchando en {servidor.DireccionActiva}, que no es loopback.");
            Assert.True(servidor.PuertoActivo > 0, "No se asignó un puerto real de escucha.");
        }
        finally
        {
            await servidor.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StartAsync_Deshabilitado_NoAbreNingunPuerto()
    {
        // Puerto libre real: si el servidor llegara a abrir la escucha, lo haría ahí y la
        // petición prosperaría. Así el test comprueba de verdad que no se conecta a nada,
        // en lugar de fallar por una URL inventada.
        int puertoLibre = ObtenerPuertoLibre();
        ConfiguracionEstado configuracion = new()
        {
            Habilitado = false,
            Direccion = "127.0.0.1",
            Puerto = puertoLibre,
        };
        ServidorEstado servidor = NuevoServidor(configuracion, NuevoEstado());

        await servidor.StartAsync(CancellationToken.None);
        try
        {
            Assert.Null(servidor.DireccionActiva);
            Assert.Equal(0, servidor.PuertoActivo);

            // Con el endpoint deshabilitado no hay nadie escuchando, pero eso se manifiesta
            // de dos formas según el sistema: conexión rechazada de inmediato
            // (HttpRequestException) o conexión que se queda colgada hasta expirar el timeout
            // (TaskCanceledException). Ambas valen; lo que no vale es que la petición
            // prospere, así que se comprueba además que ninguna de las dos es un éxito.
            Exception error = await Assert.ThrowsAnyAsync<Exception>(() =>
                ObtenerAsync(
                    $"http://127.0.0.1:{puertoLibre.ToString(CultureInfo.InvariantCulture)}/estado",
                    TimeSpan.FromMilliseconds(1500)));

            Assert.True(EsFalloPorSinEscucha(error),
                $"La petición falló por un motivo distinto de 'no hay nadie escuchando' " +
                $"({error.GetType().Name}): {error.Message}");
        }
        finally
        {
            await servidor.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Verdadero cuando el error significa "no hay nadie escuchando": conexión rechazada
    /// (<see cref="HttpRequestException"/> o <see cref="System.Net.Sockets.SocketException"/>
    /// envuelta) o conexión que expira por falta de respuesta
    /// (<see cref="TaskCanceledException"/>). Recorre la cadena de excecciones internas
    /// porque el runtime unas veces las envuelve y otras no.
    /// </summary>
    private static bool EsFalloPorSinEscucha(Exception error)
    {
        for (Exception? actual = error; actual != null; actual = actual.InnerException)
        {
            if (actual is HttpRequestException
                || actual is TaskCanceledException
                || actual is System.Net.Sockets.SocketException)
            {
                return true;
            }
        }

        return false;
    }

    // ─── (b) Los secretos no aparecen en la respuesta del endpoint ───

    [Fact]
    public void Sanear_SustituyeTodosLosSecretos()
    {
        SaneadorSecretos saneador = SaneadorDePrueba();

        string resultado = saneador.Sanear(
            $"autenticacion con {ClaveLlm} y {ClaveBitbucket} para {UsuarioBitbucket}");

        Assert.DoesNotContain(ClaveLlm, resultado, StringComparison.Ordinal);
        Assert.DoesNotContain(ClaveBitbucket, resultado, StringComparison.Ordinal);
        Assert.DoesNotContain(UsuarioBitbucket, resultado, StringComparison.Ordinal);
        Assert.Contains(SaneadorSecretos.Marca, resultado, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanear_TextoSinSecretos_SeConservaIntacto()
    {
        SaneadorSecretos saneador = SaneadorDePrueba();
        const string texto = "PR workspace/repo#12 revisado correctamente";

        Assert.Equal(texto, saneador.Sanear(texto));
    }

    [Fact]
    public void Constructor_IgnoraValoresVacios_YCuentaLosValidos()
    {
        SaneadorSecretos saneador = new(new string?[]
        {
            null,
            string.Empty,
            "   ",
            ClaveLlm,
            ClaveLlm,
        });

        Assert.Equal(1, saneador.CantidadSecretos);
    }

    [Fact]
    public void Ninguno_NoModificaElTexto()
    {
        Assert.Equal("nada que enmascarar", SaneadorSecretos.Ninguno.Sanear("nada que enmascarar"));
        Assert.False(SaneadorSecretos.Ninguno.ContieneSecreto("cualquier cosa"));
    }

    [Fact]
    public void ContieneSecreto_DetectaPresenciaEnClaro()
    {
        SaneadorSecretos saneador = SaneadorDePrueba();

        Assert.True(saneador.ContieneSecreto($"header Authorization: {ClaveBitbucket}"));
        Assert.False(saneador.ContieneSecreto($"header Authorization: {SaneadorSecretos.Marca}"));
    }

    [Fact]
    public void ConstruirCuerpo_ErrorConSecreto_NoLoPublica()
    {
        EstadoServicio estado = NuevoEstado();
        estado.RegistrarError($"no se pudo autenticar con {ClaveLlm}");

        ServidorEstado servidor = NuevoServidor(new ConfiguracionEstado { Puerto = 0 }, estado);
        string cuerpo = servidor.ConstruirCuerpo();

        Assert.DoesNotContain(ClaveLlm, cuerpo, StringComparison.Ordinal);
        Assert.Contains(SaneadorSecretos.Marca, cuerpo, StringComparison.Ordinal);
        Assert.Contains("no se pudo autenticar con", cuerpo, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstruirCuerpo_SecretosQueNoEstanEnErrores_TambienSeEnmascaran()
    {
        // El enmascarado se aplica al JSON completo: un secreto que llegue por una vía
        // inesperada (un nombre de repositorio, un mensaje del sistema) no se publica.
        EstadoServicio estado = NuevoEstado();
        estado.RegistrarError($"fallo al leer repositorio {UsuarioBitbucket}/privado con clave {ClaveBitbucket}");

        ServidorEstado servidor = NuevoServidor(new ConfiguracionEstado { Puerto = 0 }, estado);
        string cuerpo = servidor.ConstruirCuerpo();

        Assert.DoesNotContain(UsuarioBitbucket, cuerpo, StringComparison.Ordinal);
        Assert.DoesNotContain(ClaveBitbucket, cuerpo, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstruirCuerpo_ExponeContadoresYEscuchaPublicaFalsa()
    {
        EstadoServicio estado = NuevoEstado();
        estado.RegistrarVuelta(new ResultadoVuelta { PrsRevisados = 3, PrsOmitidos = 2, PrsFallidos = 1 });
        estado.AnunciarProximoSondeo(TimeSpan.FromMinutes(5));

        ServidorEstado servidor = NuevoServidor(new ConfiguracionEstado { Puerto = 0 }, estado);
        string cuerpo = servidor.ConstruirCuerpo();

        Assert.Contains("\"servicio\":\"revisor-prs\"", cuerpo, StringComparison.Ordinal);
        Assert.Contains("\"revisadosUltimaVuelta\":3", cuerpo, StringComparison.Ordinal);
        Assert.Contains("\"omitidosUltimaVuelta\":2", cuerpo, StringComparison.Ordinal);
        Assert.Contains("\"fallidosUltimaVuelta\":1", cuerpo, StringComparison.Ordinal);
        Assert.Contains("\"revisadosAcumulados\":3", cuerpo, StringComparison.Ordinal);
        Assert.Contains("\"publica\":false", cuerpo, StringComparison.Ordinal);
        Assert.Contains("2025-01-15T10:05:00+00:00", cuerpo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Endpoint_PorHttp_NoDevuelveSecretos()
    {
        EstadoServicio estado = NuevoEstado();
        estado.RegistrarVuelta(new ResultadoVuelta { PrsRevisados = 1, PrsOmitidos = 0, PrsFallidos = 1 });
        estado.RegistrarError($"revocada la clave {ClaveLlm} del usuario {UsuarioBitbucket}");

        ConfiguracionEstado configuracion = new() { Direccion = "127.0.0.1", Puerto = 0 };
        ServidorEstado servidor = NuevoServidor(configuracion, estado);

        await servidor.StartAsync(CancellationToken.None);
        try
        {
            HttpResponseMessage respuesta = await ObtenerAsync(servidor.UrlBase.TrimEnd('/') + ServidorEstado.RutaEstado);

            Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
            string cuerpo = await respuesta.Content.ReadAsStringAsync();

            Assert.DoesNotContain(ClaveLlm, cuerpo, StringComparison.Ordinal);
            Assert.DoesNotContain(UsuarioBitbucket, cuerpo, StringComparison.Ordinal);
            Assert.Contains(SaneadorSecretos.Marca, cuerpo, StringComparison.Ordinal);
            Assert.Equal("application/json", respuesta.Content.Headers.ContentType?.MediaType);
            Assert.True(respuesta.Headers.CacheControl?.NoStore ?? false,
                "La respuesta del endpoint debe declararse no almacenable (Cache-Control: no-store).");
        }
        finally
        {
            await servidor.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Endpoint_RutaDesconocida_Devuelve404()
    {
        ServidorEstado servidor = NuevoServidor(new ConfiguracionEstado { Puerto = 0 }, NuevoEstado());

        await servidor.StartAsync(CancellationToken.None);
        try
        {
            HttpResponseMessage respuesta = await ObtenerAsync(servidor.UrlBase.TrimEnd('/') + "/otra-ruta");
            Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        }
        finally
        {
            await servidor.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Endpoint_NoLoopback_NoRespondePorOtraInterfaz()
    {
        // Barrera práctica: aunque alguien intente alcanzar el puerto por la IP de red
        // de la máquina, el socket nunca se abrió ahí, así que la conexión falla.
        ServidorEstado servidor = NuevoServidor(
            new ConfiguracionEstado { Direccion = "127.0.0.1", Puerto = 0 },
            NuevoEstado());

        await servidor.StartAsync(CancellationToken.None);
        try
        {
            IPAddress? publica = ObtenerDireccionDeRed();
            if (publica is null)
            {
                // Máquina sin interfaz de red (CI aislada): no hay nada que intentar.
                return;
            }

            Assert.False(IPAddress.IsLoopback(publica));

            using var cliente = new HttpClient { Timeout = TimeSpan.FromMilliseconds(1500) };
            await Assert.ThrowsAnyAsync<Exception>(() =>
                cliente.GetAsync($"http://{publica}:{servidor.PuertoActivo.ToString()}{ServidorEstado.RutaEstado}"));
        }
        finally
        {
            await servidor.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<HttpResponseMessage> ObtenerAsync(
        string url,
        TimeSpan? timeout = null)
    {
        using var cliente = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(5) };
        return await cliente.GetAsync(url);
    }

    /// <summary>
    /// Reserva un puerto efímero en loopback y lo libera, para tener una dirección donde
    /// saber que no hay nadie escuchando.
    /// </summary>
    private static int ObtenerPuertoLibre()
    {
        var prueba = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        prueba.Start();
        int puerto = ((IPEndPoint)prueba.LocalEndpoint).Port;
        prueba.Stop();
        return puerto;
    }

    private static IPAddress? ObtenerDireccionDeRed()
    {
        foreach (IPAddress ip in Dns.GetHostAddresses(Dns.GetHostName()))
        {
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
            {
                return ip;
            }
        }

        return null;
    }
}