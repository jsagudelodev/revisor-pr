using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RevisorPrs.Servicio;
using Xunit;

namespace RevisorPrs.Tests;

/// <summary>
/// Pruebas de las convenciones por repositorio (B3).
///
/// Un único prompt idéntico para todos los repositorios convierte al revisor en un bot
/// genérico. Con un fichero de guía dentro del propio repositorio, cada equipo ajusta el
/// criterio sin tocar el servicio ni pedírselo a quien lo opera: lo edita en un pull
/// request suyo, como cualquier otro cambio.
/// </summary>
public class GuiaRepositorioTests
{
    private const string Guia = "No usamos excepciones para flujo de control. Los tests van en Given/When/Then.";

    // ---------- La guía sale de la rama de destino, no del pull request ----------

    [Fact]
    public async Task LaGuiaSeLeeDeLaRamaDeDestino_NoDeLaRamaDelPullRequest()
    {
        // Si se leyera del propio PR, cualquiera podría incluir en su pull request una
        // guía que dijese "no reportes nada" y desactivar su propia revisión. En la rama
        // de destino, cambiarla exige pasar por una revisión del equipo.
        var cliente = new ClienteConGuia(Guia);
        var revisor = new RevisorQueCaptura();

        await Ejecutar(cliente, revisor);

        Assert.Equal("main", cliente.ReferenciaPedida);
        Assert.NotEqual("rama-del-pr", cliente.ReferenciaPedida);
    }

    [Fact]
    public async Task LaGuiaLlegaAlRevisor()
    {
        var cliente = new ClienteConGuia(Guia);
        var revisor = new RevisorQueCaptura();

        await Ejecutar(cliente, revisor);

        Assert.Equal(Guia, revisor.ContextoRecibido!.Guia);
        Assert.True(revisor.ContextoRecibido.TieneGuia);
    }

    [Fact]
    public async Task SinGuiaEnElRepositorio_SeRevisaIgual()
    {
        // Que no haya guía es el caso normal, no un error.
        var cliente = new ClienteConGuia(null);
        var revisor = new RevisorQueCaptura();

        await Ejecutar(cliente, revisor);

        Assert.NotNull(revisor.ContextoRecibido);
        Assert.False(revisor.ContextoRecibido!.TieneGuia);
    }

    [Fact]
    public async Task ConRutaDeGuiaVacia_NiSiquieraSePregunta()
    {
        var cliente = new ClienteConGuia(Guia);
        var revisor = new RevisorQueCaptura();

        await Ejecutar(cliente, revisor, new ConfiguracionBitbucket { RutaGuiaRepositorio = "" });

        Assert.Equal(0, cliente.LecturasDeArchivo);
    }

    [Fact]
    public async Task LaGuiaSeLeeUnaVezPorRepositorioYRama_NoUnaPorPullRequest()
    {
        // Tres pull requests contra main comparten exactamente la misma guía: pedirla
        // tres veces serían dos llamadas tiradas en cada vuelta de sondeo.
        var cliente = new ClienteConGuia(Guia, new[]
        {
            new EventoPr("equipo-a/repo-1", 1, "c1", "t1", "main"),
            new EventoPr("equipo-a/repo-1", 2, "c2", "t2", "main"),
            new EventoPr("equipo-a/repo-1", 3, "c3", "t3", "main"),
        });

        await Ejecutar(cliente, new RevisorQueCaptura());

        Assert.Equal(1, cliente.LecturasDeArchivo);
    }

    [Fact]
    public async Task UnaGuiaEnormeSeRecorta()
    {
        // Una guía sin límite desplazaría al propio diff dentro de la ventana del modelo.
        var cliente = new ClienteConGuia(new string('g', 20_000));
        var revisor = new RevisorQueCaptura();

        await Ejecutar(cliente, revisor);

        Assert.Equal(8000, revisor.ContextoRecibido!.Guia!.Length);
    }

    // ---------- La guía en el prompt ----------

    [Fact]
    public async Task ElPromptLlevaLaGuiaFueraDeLasMarcasDeMaterialDelAutor()
    {
        // La guía vive en la rama de destino y pasó por revisión del equipo, así que no
        // es material no confiable como el título, la descripción o el diff.
        var handler = new CapturadorDeMensaje();
        var revisor = new Revisor(
            new HttpClient(handler),
            Options.Create(new ConfiguracionLlm { Endpoint = "https://x.invalid/v1", Modelo = "m" }),
            NullLogger<Revisor>.Instance);

        await revisor.RevisarAsync("el diff", new ContextoRevision("un título", null, Guia));

        string enviado = handler.Mensaje!;
        int posGuia = enviado.IndexOf(Guia, StringComparison.Ordinal);
        int posPrimeraMarca = enviado.IndexOf("<<<MATERIAL_A_REVISAR>>>", StringComparison.Ordinal);

        Assert.True(posGuia >= 0, "La guía debe llegar al modelo.");
        Assert.True(posGuia < posPrimeraMarca, "La guía va antes del material del autor.");
        Assert.Contains("Convenciones acordadas por el equipo", enviado, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SinGuia_ElPromptNoMencionaConvenciones()
    {
        var handler = new CapturadorDeMensaje();
        var revisor = new Revisor(
            new HttpClient(handler),
            Options.Create(new ConfiguracionLlm { Endpoint = "https://x.invalid/v1", Modelo = "m" }),
            NullLogger<Revisor>.Instance);

        await revisor.RevisarAsync("el diff", new ContextoRevision("un título", null));

        Assert.DoesNotContain("Convenciones acordadas", handler.Mensaje!, StringComparison.Ordinal);
    }

    // ---------- El cliente contra la API ----------

    [Fact]
    public async Task ObtenerArchivo_PideLaRutaEnLaReferenciaIndicada()
    {
        var handler = new CapturadorDeUrl(HttpStatusCode.OK, "contenido");
        var cliente = CrearClienteReal(handler);

        string? contenido = await cliente.ObtenerArchivo("equipo-a/repo-1", "main", ".revisorpr.md");

        Assert.Equal("contenido", contenido);
        Assert.Contains("/repositories/equipo-a/repo-1/src/main/.revisorpr.md", handler.Url!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObtenerArchivo_SiNoExiste_DevuelveNuloSinLanzar()
    {
        // Un 404 significa "este repositorio no tiene guía", que es el caso mayoritario.
        var handler = new CapturadorDeUrl(HttpStatusCode.NotFound, "");
        var cliente = CrearClienteReal(handler);

        Assert.Null(await cliente.ObtenerArchivo("equipo-a/repo-1", "main", ".revisorpr.md"));
    }

    [Fact]
    public void ConfiguracionBitbucket_TraeRutaDeGuiaPorDefecto()
    {
        Assert.Equal(".revisorpr.md", new ConfiguracionBitbucket().RutaGuiaRepositorio);
    }

    // ---------- Utilidades ----------

    private static ClienteBitbucket CrearClienteReal(HttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new ConfiguracionBitbucket { Usuario = "u", ClaveAplicacion = "c" }),
            NullLogger<ClienteBitbucket>.Instance,
            new TraductorEventoPr(NullLogger<TraductorEventoPr>.Instance));

    private static async Task Ejecutar(
        ClienteConGuia cliente,
        IRevisor revisor,
        ConfiguracionBitbucket? config = null)
    {
        var decisor = new DecisorRevisar(new RegistradorFalso<DecisorRevisar>());
        decisor.FiltrarPrsParaRevisar(Array.Empty<PullRequest>());

        var sut = new EjecutorVuelta(
            new RegistradorFalso<EjecutorVuelta>(),
            cliente,
            decisor,
            revisor,
            new AlmacenNulo(),
            new ConfiguracionSondeo { Repositorios = new[] { "equipo-a/repo-1" } },
            ahora: () => DateTimeOffset.UnixEpoch,
            configuracionBitbucket: config ?? new ConfiguracionBitbucket());

        await sut.EjecutarAsync(CancellationToken.None);
    }

    private sealed class ClienteConGuia : IClienteBitbucket
    {
        private readonly string? _guia;
        private readonly EventoPr[] _prs;

        public ClienteConGuia(string? guia, EventoPr[]? prs = null)
        {
            _guia = guia;
            // La rama de destino es "main"; la del pull request seria otra.
            _prs = prs ?? new[] { new EventoPr("equipo-a/repo-1", 1, "c1", "titulo", "main") };
        }

        public string? ReferenciaPedida { get; private set; }
        public int LecturasDeArchivo { get; private set; }

        public Task<IEnumerable<EventoPr>> ListarPrsAbiertos(string repositorio, CancellationToken cancelacion = default)
            => Task.FromResult<IEnumerable<EventoPr>>(_prs);

        public Task<string> ObtenerDiff(string repositorio, int numero, CancellationToken cancelacion = default)
            => Task.FromResult("diff --git a/A.cs b/A.cs\n@@ -1,1 +1,1 @@\n+x\n");

        public Task PublicarComentario(string repositorio, int numero, Hallazgo hallazgo, CancellationToken cancelacion = default)
            => Task.CompletedTask;

        public Task PublicarComentarioGeneral(string repositorio, int numero, string texto, CancellationToken cancelacion = default)
            => Task.CompletedTask;

        public Task<string?> ObtenerArchivo(string repositorio, string referencia, string ruta, CancellationToken cancelacion = default)
        {
            LecturasDeArchivo++;
            ReferenciaPedida = referencia;
            return Task.FromResult(_guia);
        }
    }

    private sealed class RevisorQueCaptura : IRevisor
    {
        public ContextoRevision? ContextoRecibido { get; private set; }

        public Task<ResultadoRevision> RevisarAsync(string diff, ContextoRevision? contexto = null, CancellationToken token = default)
        {
            ContextoRecibido = contexto;
            return Task.FromResult(ResultadoRevision.Ok(Array.Empty<Hallazgo>()));
        }
    }

    private sealed class CapturadorDeMensaje : HttpMessageHandler
    {
        public string? Mensaje { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            foreach (var m in doc.RootElement.GetProperty("messages").EnumerateArray())
            {
                if (m.GetProperty("role").GetString() == "user")
                {
                    Mensaje = m.GetProperty("content").GetString();
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"content":"{\"hallazgos\":[]}"}}]}""",
                    Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class CapturadorDeUrl : HttpMessageHandler
    {
        private readonly HttpStatusCode _codigo;
        private readonly string _cuerpo;

        public CapturadorDeUrl(HttpStatusCode codigo, string cuerpo)
        {
            _codigo = codigo;
            _cuerpo = cuerpo;
        }

        public string? Url { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(_codigo)
            {
                Content = new StringContent(_cuerpo, Encoding.UTF8, "text/plain"),
            });
        }
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
}
