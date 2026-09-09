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
/// Pruebas del contexto que recibe el modelo (B2).
///
/// Antes solo veía el diff: ni la intención declarada del pull request ni el código
/// alrededor del cambio. Sin lo primero no distingue un cambio deliberado que el autor
/// explica de un descuido; sin lo segundo señala como errores cosas definidas unas
/// líneas por encima del recorte.
/// </summary>
public class ContextoRevisionTests
{
    // ---------- Intención del pull request en el prompt ----------

    private static (Revisor Revisor, CapturadorDePeticiones Handler) CrearRevisor()
    {
        var handler = new CapturadorDePeticiones();
        var cliente = new HttpClient(handler);
        var config = Options.Create(new ConfiguracionLlm
        {
            Endpoint = "https://api.ejemplo.invalid/v1/chat/completions",
            Modelo = "modelo",
        });

        return (new Revisor(cliente, config, NullLogger<Revisor>.Instance), handler);
    }

    [Fact]
    public async Task RevisarAsync_ConTituloYDescripcion_LosMandaAlModelo()
    {
        var (revisor, handler) = CrearRevisor();

        await revisor.RevisarAsync(
            "diff --git a/A.cs b/A.cs",
            new ContextoRevision("Arreglar el cálculo del IVA", "El redondeo se hacía al final en vez de por línea."));

        string enviado = handler.UltimoMensajeDeUsuario!;
        Assert.Contains("Título: Arreglar el cálculo del IVA", enviado, StringComparison.Ordinal);
        Assert.Contains("El redondeo se hacía al final", enviado, StringComparison.Ordinal);
        Assert.Contains("diff --git a/A.cs b/A.cs", enviado, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevisarAsync_SinContexto_NoAnadeSeccionDeIntencion()
    {
        var (revisor, handler) = CrearRevisor();

        await revisor.RevisarAsync("diff --git a/A.cs b/A.cs");

        string enviado = handler.UltimoMensajeDeUsuario!;
        Assert.DoesNotContain("Intención declarada", enviado, StringComparison.Ordinal);
        Assert.Contains("diff --git a/A.cs b/A.cs", enviado, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevisarAsync_ConSoloTitulo_NoDejaBloqueVacioDeDescripcion()
    {
        var (revisor, handler) = CrearRevisor();

        await revisor.RevisarAsync("diff", new ContextoRevision("Solo título", null));

        string enviado = handler.UltimoMensajeDeUsuario!;
        Assert.Contains("Título: Solo título", enviado, StringComparison.Ordinal);
        Assert.DoesNotContain("Descripción:", enviado, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevisarAsync_ElMaterialDelAutorVaDelimitado()
    {
        // El título, la descripción y el diff los escribe quien abre el PR: son texto no
        // confiable y el modelo tiene que poder distinguirlos de sus instrucciones.
        var (revisor, handler) = CrearRevisor();

        await revisor.RevisarAsync("el diff", new ContextoRevision("un título", "una descripción"));

        string enviado = handler.UltimoMensajeDeUsuario!;
        Assert.Equal(2, ContarApariciones(enviado, "<<<MATERIAL_A_REVISAR>>>"));
        Assert.Equal(2, ContarApariciones(enviado, "<<<FIN_MATERIAL_A_REVISAR>>>"));
    }

    [Fact]
    public void ContextoRevision_Vacio_NoAportaNada()
    {
        Assert.False(ContextoRevision.Ninguno.TieneAlgo);
        Assert.False(new ContextoRevision("   ", "  ").TieneAlgo);
        Assert.True(new ContextoRevision("algo", null).TieneAlgo);
        Assert.True(new ContextoRevision(null, "algo").TieneAlgo);
    }

    // ---------- La intención llega desde Bitbucket hasta el modelo ----------

    [Fact]
    public void Traductor_ExtraeLaDescripcionDelPullRequest()
    {
        var json = JsonDocument.Parse("""
            {
              "id": 42,
              "title": "Arreglar el IVA",
              "description": "El redondeo se hacía al final.",
              "links": { "html": { "href": "https://bitbucket.org/equipo/repo/pull-requests/42" } },
              "source": { "commit": { "hash": "abc123" } },
              "destination": { "branch": { "name": "main" } }
            }
            """).RootElement;

        var evento = new TraductorEventoPr(NullLogger<TraductorEventoPr>.Instance).Traducir(json);

        Assert.NotNull(evento);
        Assert.Equal("El redondeo se hacía al final.", evento!.Descripcion);
    }

    [Fact]
    public void Traductor_SinDescripcion_SigueSiendoValido()
    {
        // Muchos equipos dejan la descripción vacía: no puede invalidar el pull request.
        var json = JsonDocument.Parse("""
            {
              "id": 42,
              "title": "Arreglar el IVA",
              "links": { "html": { "href": "https://bitbucket.org/equipo/repo/pull-requests/42" } },
              "source": { "commit": { "hash": "abc123" } },
              "destination": { "branch": { "name": "main" } }
            }
            """).RootElement;

        var evento = new TraductorEventoPr(NullLogger<TraductorEventoPr>.Instance).Traducir(json);

        Assert.NotNull(evento);
        Assert.Null(evento!.Descripcion);
    }

    [Fact]
    public async Task EjecutorVuelta_PasaLaIntencionDelPrAlRevisor()
    {
        // El cableado: el título y la descripción tienen que sobrevivir al salto de
        // EventoPr a PullRequest y llegar al revisor.
        var cliente = new ClienteConPr(new EventoPr(
            "equipo-a/repo-1", 7, "commit-1", "Arreglar el IVA", "main", "Redondeo por línea."));

        var revisor = new RevisorQueCapturaContexto();

        var decisor = new DecisorRevisar(new RegistradorFalso<DecisorRevisar>());
        decisor.FiltrarPrsParaRevisar(Array.Empty<PullRequest>());

        var sut = new EjecutorVuelta(
            new RegistradorFalso<EjecutorVuelta>(),
            cliente,
            decisor,
            revisor,
            new AlmacenNulo(),
            new ConfiguracionSondeo { Repositorios = new[] { "equipo-a/repo-1" } },
            ahora: () => DateTimeOffset.UnixEpoch);

        await sut.EjecutarAsync(CancellationToken.None);

        Assert.NotNull(revisor.ContextoRecibido);
        Assert.Equal("Arreglar el IVA", revisor.ContextoRecibido!.Titulo);
        Assert.Equal("Redondeo por línea.", revisor.ContextoRecibido.Descripcion);
    }

    // ---------- Líneas de contexto pedidas a Bitbucket ----------

    [Fact]
    public async Task ObtenerDiff_PideLineasDeContextoALaApi()
    {
        var handler = new CapturadorDeUrl();
        var cliente = new ClienteBitbucket(
            new HttpClient(handler),
            Options.Create(new ConfiguracionBitbucket
            {
                Usuario = "u",
                ClaveAplicacion = "c",
                LineasDeContexto = 10,
            }),
            NullLogger<ClienteBitbucket>.Instance,
            new TraductorEventoPr(NullLogger<TraductorEventoPr>.Instance));

        await cliente.ObtenerDiff("workspace/repo", 42);

        // Sin contexto el modelo no ve lo que rodea al cambio, que es de donde salen
        // buena parte de los falsos positivos.
        Assert.Contains("context=10", handler.UltimaUrl!, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfiguracionBitbucket_TraeContextoPorDefecto()
    {
        // La promesa es que funcione bien sin configurar nada.
        Assert.Equal(10, new ConfiguracionBitbucket().LineasDeContexto);
    }

    // ---------- Utilidades ----------

    private static int ContarApariciones(string texto, string aguja)
    {
        int n = 0, i = 0;
        while ((i = texto.IndexOf(aguja, i, StringComparison.Ordinal)) >= 0)
        {
            n++;
            i += aguja.Length;
        }
        return n;
    }

    private sealed class CapturadorDePeticiones : HttpMessageHandler
    {
        public string? UltimoMensajeDeUsuario { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string cuerpo = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(cuerpo);

            foreach (var mensaje in doc.RootElement.GetProperty("messages").EnumerateArray())
            {
                if (mensaje.GetProperty("role").GetString() == "user")
                {
                    UltimoMensajeDeUsuario = mensaje.GetProperty("content").GetString();
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"content":"{\"hallazgos\":[]}"}}]}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed class CapturadorDeUrl : HttpMessageHandler
    {
        public string? UltimaUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            UltimaUrl = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("diff", Encoding.UTF8, "text/plain"),
            });
        }
    }

    private sealed class ClienteConPr : IClienteBitbucket
    {
        private readonly EventoPr _pr;

        public ClienteConPr(EventoPr pr) => _pr = pr;

        public Task<IEnumerable<EventoPr>> ListarPrsAbiertos(string repositorio, CancellationToken cancelacion = default)
            => Task.FromResult<IEnumerable<EventoPr>>(new[] { _pr });

        public Task<string> ObtenerDiff(string repositorio, int numero, CancellationToken cancelacion = default)
            => Task.FromResult("diff --git a/A.cs b/A.cs\n@@ -1,1 +1,1 @@\n+x\n");

        public Task PublicarComentario(string repositorio, int numero, Hallazgo hallazgo, CancellationToken cancelacion = default)
            => Task.CompletedTask;

        public Task PublicarComentarioGeneral(string repositorio, int numero, string texto, CancellationToken cancelacion = default)
            => Task.CompletedTask;
    }

    private sealed class RevisorQueCapturaContexto : IRevisor
    {
        public ContextoRevision? ContextoRecibido { get; private set; }

        public Task<ResultadoRevision> RevisarAsync(string diff, ContextoRevision? contexto = null, CancellationToken token = default)
        {
            ContextoRecibido = contexto;
            return Task.FromResult(ResultadoRevision.Ok(Array.Empty<Hallazgo>()));
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
