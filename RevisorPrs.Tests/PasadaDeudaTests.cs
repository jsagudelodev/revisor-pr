using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RevisorPrs.Servicio;
using Xunit;

namespace RevisorPrs.Tests;

/// <summary>
/// Pruebas de la pasada de diagnóstico y deuda: C4, C5, D1, D5 y D6.
/// </summary>
public class PasadaDeudaTests
{
    // ---------- C4: los fallos de listado se ven en /estado ----------

    [Fact]
    public async Task UnFalloAlListarUnRepositorio_LlegaAlEstado()
    {
        // Es el modo de fallo más probable en producción —credenciales caducadas— y el
        // endpoint hecho para diagnosticarlo devolvía "ultimosErrores": [].
        var estado = new EstadoServicio();

        var sut = new EjecutorVuelta(
            new RegistradorFalso<EjecutorVuelta>(),
            new ClienteQueNoPuedeListar(),
            new DecisorRevisar(new RegistradorFalso<DecisorRevisar>()),
            new RevisorNulo(),
            new AlmacenNulo(),
            new ConfiguracionSondeo { Repositorios = new[] { "equipo-a/repo-1" } },
            ahora: () => DateTimeOffset.UnixEpoch,
            estado: estado);

        await sut.EjecutarAsync(CancellationToken.None);

        var error = Assert.Single(estado.Capturar().UltimosErrores);
        Assert.Contains("equipo-a/repo-1", error.Mensaje, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ElFalloDeListadoQueLlegaAlEstado_VaSaneadoDeSecretos()
    {
        var estado = new EstadoServicio();
        var saneador = new SaneadorSecretos(new[] { "clave-de-aplicacion-larga" });

        var sut = new EjecutorVuelta(
            new RegistradorFalso<EjecutorVuelta>(),
            new ClienteQueNoPuedeListar("401 con clave-de-aplicacion-larga"),
            new DecisorRevisar(new RegistradorFalso<DecisorRevisar>()),
            new RevisorNulo(),
            new AlmacenNulo(),
            new ConfiguracionSondeo { Repositorios = new[] { "equipo-a/repo-1" } },
            ahora: () => DateTimeOffset.UnixEpoch,
            estado: estado,
            saneador: saneador);

        await sut.EjecutarAsync(CancellationToken.None);

        var error = Assert.Single(estado.Capturar().UltimosErrores);
        Assert.DoesNotContain("clave-de-aplicacion-larga", error.Mensaje, StringComparison.Ordinal);
    }

    // ---------- C5: el log respeta su configuración ----------

    [Theory]
    [InlineData(LogLevel.Trace)]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.Warning)]
    [InlineData(LogLevel.Error)]
    public void ElLogNoImponeUnUmbralPropio(LogLevel nivel)
    {
        // Antes había un suelo fijo en Information que ignoraba Logging:LogLevel: el
        // operador no podía bajar el nivel para diagnosticar. El filtrado por
        // configuración lo aplica el ILoggerFactory antes de llegar al proveedor.
        using var carpeta = new CarpetaTemporal();
        var proveedor = new ProveedorRegistrosRotativo(
            new ConfiguracionRegistro { RutaFichero = carpeta.Ruta("log.txt") });

        Assert.True(proveedor.CreateLogger("x").IsEnabled(nivel));
    }

    [Fact]
    public void ElLogSigueSinAdmitirElNivelNone()
    {
        using var carpeta = new CarpetaTemporal();
        var proveedor = new ProveedorRegistrosRotativo(
            new ConfiguracionRegistro { RutaFichero = carpeta.Ruta("log.txt") });

        Assert.False(proveedor.CreateLogger("x").IsEnabled(LogLevel.None));
    }

    [Fact]
    public void ElFicheroDeLogSePuedeLeerMientrasElServicioEscribe()
    {
        // Se probó a mantener el fichero abierto para ahorrar aperturas y se descartó:
        // en Windows, un fichero abierto para escritura no lo puede leer una herramienta
        // que abra con FileShare.Read (Bloc de notas, "type", File.ReadAllText). El
        // operador necesita leer el log justo mientras el servicio corre.
        using var carpeta = new CarpetaTemporal();
        string ruta = carpeta.Ruta("log.txt");
        var proveedor = new ProveedorRegistrosRotativo(new ConfiguracionRegistro { RutaFichero = ruta });
        var log = proveedor.CreateLogger("prueba");

        log.LogInformation("primera linea");

        string contenido = System.IO.File.ReadAllText(ruta);
        Assert.Contains("primera linea", contenido, StringComparison.Ordinal);

        // Y se puede seguir escribiendo después de haberlo leído.
        log.LogInformation("segunda linea");
        Assert.Contains("segunda linea", System.IO.File.ReadAllText(ruta), StringComparison.Ordinal);
    }

    // ---------- D5: la recuperación de JSON truncado ya no es cuadrática ----------

    [Fact]
    public async Task UnaRespuestaLargaTruncada_SeRecuperaSinTardarUnaEternidad()
    {
        // Con la versión anterior —recorrer de derecha a izquierda reparseando el prefijo
        // entero en cada punto de corte— el coste crecía con el cuadrado del tamaño,
        // justo en el caso de una respuesta larga que ya venía mal.
        var hallazgos = string.Join(",", Enumerable.Range(1, 4000).Select(i =>
            $$"""{"Archivo":"src/A{{i}}.cs","Linea":{{i}},"Severidad":"error","Resumen":"r{{i}}","Detalle":"d{{i}}"}"""));

        string truncada = "{\"hallazgos\":[" + hallazgos + ",{\"Archivo\":\"src/Corta";

        var revisor = new Revisor(
            new HttpClient(new RespuestaFija(truncada)),
            Options.Create(new ConfiguracionLlm { Endpoint = "https://x.invalid/v1", Modelo = "m" }),
            NullLogger<Revisor>.Instance);

        var reloj = Stopwatch.StartNew();
        var resultado = await revisor.RevisarAsync("diff");
        reloj.Stop();

        Assert.True(resultado.Exito);
        Assert.Equal(4000, resultado.Hallazgos.Count);
        Assert.Contains("4000 hallazgo(s) recuperado(s)", resultado.Motivo!, StringComparison.Ordinal);
        Assert.True(reloj.ElapsedMilliseconds < 3000, $"Tardó {reloj.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task UnTruncadoConLlavesDentroDeCadenas_NoSeConfunde()
    {
        // Las llaves dentro de una cadena no abren ni cierran estructura, y una comilla
        // escapada no termina la cadena.
        string truncada =
            """{"hallazgos":[{"Archivo":"a.cs","Linea":1,"Severidad":"error","Resumen":"usa } y \" aqui","Detalle":"{"}""";

        var revisor = new Revisor(
            new HttpClient(new RespuestaFija(truncada)),
            Options.Create(new ConfiguracionLlm { Endpoint = "https://x.invalid/v1", Modelo = "m" }),
            NullLogger<Revisor>.Instance);

        var resultado = await revisor.RevisarAsync("diff");

        Assert.True(resultado.Exito);
        var hallazgo = Assert.Single(resultado.Hallazgos);
        Assert.Equal("usa } y \" aqui", hallazgo.Resumen);
    }

    [Fact]
    public async Task UnTruncadoSinNingunHallazgoEntero_NoInventaNada()
    {
        var revisor = new Revisor(
            new HttpClient(new RespuestaFija("""{"hallazgos":[{"Archivo":"a.cs","Lin""")),
            Options.Create(new ConfiguracionLlm { Endpoint = "https://x.invalid/v1", Modelo = "m" }),
            NullLogger<Revisor>.Instance);

        var resultado = await revisor.RevisarAsync("diff");

        Assert.True(resultado.Exito);
        Assert.Empty(resultado.Hallazgos);
    }

    // ---------- D6: el motivo del truncado sobrevive al reintento ----------

    [Fact]
    public async Task TrasElReintento_ElMotivoDelTruncadoSeConserva()
    {
        // En el primer intento ya se conservaba; en el del reintento se descartaba,
        // ocultando información de diagnóstico en el camino más raro.
        var handler = new RespuestasEnCola(
            "esto es prosa, no JSON",
            """{"hallazgos":[{"Archivo":"a.cs","Linea":1,"Severidad":"error","Resumen":"r","Detalle":"d"},{"Archivo":"b""");

        var revisor = new Revisor(
            new HttpClient(handler),
            Options.Create(new ConfiguracionLlm { Endpoint = "https://x.invalid/v1", Modelo = "m" }),
            NullLogger<Revisor>.Instance);

        var resultado = await revisor.RevisarAsync("diff");

        Assert.True(resultado.Exito);
        Assert.Single(resultado.Hallazgos);
        Assert.NotNull(resultado.Motivo);
        Assert.Contains("truncado", resultado.Motivo!, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- D3: el appsettings.json de la raíz ya no existe ----------

    [Fact]
    public void NoQuedaUnAppsettingsHuerfanoEnLaRaiz()
    {
        // Estaba fuera del proyecto, no se cargaba nunca, y usaba claves que el código no
        // lee (Llm:ApiKey en vez de ClaveApi). Quien lo editara creyendo que configuraba
        // el servicio no vería ningún efecto.
        string raiz = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

        Assert.False(
            System.IO.File.Exists(System.IO.Path.Combine(raiz, "appsettings.json")),
            "El appsettings.json de la raíz es una trampa: no se carga y sus claves no coinciden.");
    }

    // ---------- Utilidades ----------

    private sealed class RespuestaFija : HttpMessageHandler
    {
        private readonly string _contenido;

        public RespuestaFija(string contenido) => _contenido = contenido;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Envolver(_contenido));
    }

    private sealed class RespuestasEnCola : HttpMessageHandler
    {
        private readonly Queue<string> _contenidos;

        public RespuestasEnCola(params string[] contenidos) => _contenidos = new Queue<string>(contenidos);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Envolver(_contenidos.Count > 0 ? _contenidos.Dequeue() : "{}"));
    }

    private static HttpResponseMessage Envolver(string contenido)
    {
        string json = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = contenido } } },
        });

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class ClienteQueNoPuedeListar : IClienteBitbucket
    {
        private readonly string _mensaje;

        public ClienteQueNoPuedeListar(string mensaje = "401 no autorizado") => _mensaje = mensaje;

        public Task<IEnumerable<EventoPr>> ListarPrsAbiertos(string repositorio, CancellationToken cancelacion = default)
            => throw new HttpRequestException(_mensaje);

        public Task<string> ObtenerDiff(string repositorio, int numero, CancellationToken cancelacion = default)
            => Task.FromResult(string.Empty);

        public Task PublicarComentario(string repositorio, int numero, Hallazgo hallazgo, CancellationToken cancelacion = default)
            => Task.CompletedTask;

        public Task PublicarComentarioGeneral(string repositorio, int numero, string texto, CancellationToken cancelacion = default)
            => Task.CompletedTask;
    }

    private sealed class RevisorNulo : IRevisor
    {
        public Task<ResultadoRevision> RevisarAsync(string diff, ContextoRevision? contexto = null, CancellationToken token = default)
            => Task.FromResult(ResultadoRevision.Ok(Array.Empty<Hallazgo>()));
    }

    private sealed class AlmacenNulo : IAlmacen
    {
        public void MarcarRevisado(string slugRepo, int idPr, string hashCommit) { }
        public bool Revisado(string slugRepo, int idPr, string hashCommit) => false;
        public IEnumerable<(string Repositorio, int Numero, string Commit)> ListarRevisiones()
            => Array.Empty<(string, int, string)>();
        public void MarcarFallido(string slugRepo, int idPr, string hashCommit, string motivo) { }
        public bool DebeReintentar(string slugRepo, int idPr, DateTimeOffset ahora) => true;
        public IEnumerable<(string Repositorio, int PullRequest, string Commit, string Motivo)> ListarFallos()
            => Array.Empty<(string, int, string, string)>();
        public bool ComentarioPublicado(string slugRepo, int idPr, string huella) => false;
        public void MarcarComentarioPublicado(string slugRepo, int idPr, string hashCommit, string huella, string comentario) { }
    }

    private sealed class CarpetaTemporal : IDisposable
    {
        private readonly string _ruta =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "revisorprs-pasada-" + Guid.NewGuid().ToString("N"));

        public CarpetaTemporal() => System.IO.Directory.CreateDirectory(_ruta);

        public string Ruta(string nombre) => System.IO.Path.Combine(_ruta, nombre);

        public void Dispose()
        {
            try { System.IO.Directory.Delete(_ruta, recursive: true); }
            catch (System.IO.IOException) { }
        }
    }
}
