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
/// Pruebas del coste visible por pull request (C3).
///
/// Sin una cifra que enseñar, la primera factura sorpresa cierra el experimento. Se mide
/// en tokens porque es lo que devuelve el proveedor y no caduca; el dinero solo se estima
/// si el equipo configura sus tarifas.
/// </summary>
public class CosteVisibleTests
{
    // ---------- El revisor lee el consumo de la respuesta ----------

    private static Revisor CrearRevisor(HttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new ConfiguracionLlm { Endpoint = "https://x.invalid/v1", Modelo = "m" }),
            NullLogger<Revisor>.Instance);

    [Fact]
    public async Task RevisarAsync_AnotaLosTokensQueInformaElProveedor()
    {
        var handler = new RespuestasEnCola(
            Respuesta("""{"hallazgos":[]}""", entrada: 1500, salida: 200));

        var resultado = await CrearRevisor(handler).RevisarAsync("diff");

        Assert.Equal(1500, resultado.Consumo.Entrada);
        Assert.Equal(200, resultado.Consumo.Salida);
        Assert.Equal(1700, resultado.Consumo.Total);
    }

    [Fact]
    public async Task RevisarAsync_ConReintento_SumaElCosteDeLosDosIntentos()
    {
        // El reintento se paga igual que el primer intento: contar solo uno mentiría
        // justo en el caso que más cuesta.
        var handler = new RespuestasEnCola(
            Respuesta("esto no es JSON", entrada: 1000, salida: 50),
            Respuesta("""{"hallazgos":[]}""", entrada: 1100, salida: 80));

        var resultado = await CrearRevisor(handler).RevisarAsync("diff");

        Assert.Equal(2100, resultado.Consumo.Entrada);
        Assert.Equal(130, resultado.Consumo.Salida);
    }

    [Fact]
    public async Task RevisarAsync_CuandoFalla_TambienInformaDelCoste()
    {
        // Una revisión que no sirvió se paga lo mismo. Ocultarla falsearía la cifra
        // justo cuando más interesa mirarla.
        var handler = new RespuestasEnCola(
            Respuesta("prosa", entrada: 1000, salida: 40),
            Respuesta("más prosa", entrada: 1000, salida: 40));

        var resultado = await CrearRevisor(handler).RevisarAsync("diff");

        Assert.False(resultado.Exito);
        Assert.Equal(2080, resultado.Consumo.Total);
    }

    [Fact]
    public async Task RevisarAsync_SiElProveedorNoInformaDelUso_NoRompe()
    {
        // Un proveedor sin bloque "usage" no puede tumbar la revisión: quedarse sin la
        // cifra de coste es peor que tenerla, pero mucho mejor que fallar.
        var handler = new RespuestasEnCola(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"content":"{\"hallazgos\":[]}"}}]}""",
                    Encoding.UTF8, "application/json"),
            });

        var resultado = await CrearRevisor(handler).RevisarAsync("diff");

        Assert.True(resultado.Exito);
        Assert.Equal(0, resultado.Consumo.Total);
    }

    // ---------- La aritmética del consumo ----------

    [Fact]
    public void ConsumoTokens_SeSuman()
    {
        var total = new ConsumoTokens(100, 20) + new ConsumoTokens(50, 5);

        Assert.Equal(150, total.Entrada);
        Assert.Equal(25, total.Salida);
        Assert.Equal(175, total.Total);
    }

    [Fact]
    public void ConsumoTokens_EstimaConLasTarifasDelEquipo()
    {
        // Un millón de tokens de entrada a 3 y medio millón de salida a 15.
        var consumo = new ConsumoTokens(1_000_000, 500_000);

        Assert.Equal(10.5m, consumo.Estimar(costePorMillonEntrada: 3m, costePorMillonSalida: 15m));
    }

    [Fact]
    public void ConsumoTokens_SinTarifas_NoEstimaNada()
    {
        Assert.Equal(0m, new ConsumoTokens(1_000_000, 500_000).Estimar(0m, 0m));
    }

    // ---------- El estado acumula ----------

    [Fact]
    public void EstadoServicio_AcumulaElConsumoDeVariasRevisiones()
    {
        var estado = new EstadoServicio();

        estado.RegistrarConsumo(new ConsumoTokens(1000, 100));
        estado.RegistrarConsumo(new ConsumoTokens(2000, 300));

        var foto = estado.Capturar();
        Assert.Equal(3400, foto.ConsumoAcumulado.Total);
        Assert.Equal(2300, foto.ConsumoUltimaRevision.Total);
        Assert.Equal(2, foto.RevisionesConCoste);
    }

    [Fact]
    public void EstadoServicio_IgnoraElConsumoVacio()
    {
        // Un proveedor que no informa no debe inflar el contador de revisiones medidas.
        var estado = new EstadoServicio();

        estado.RegistrarConsumo(ConsumoTokens.Ninguno);

        Assert.Equal(0, estado.Capturar().RevisionesConCoste);
    }

    // ---------- El endpoint lo publica ----------

    private static ServidorEstado CrearServidor(EstadoServicio estado, ConfiguracionLlm? llm = null) =>
        new(NullLogger<ServidorEstado>.Instance,
            new ConfiguracionEstado { Puerto = 0 },
            estado,
            saneador: null,
            configuracionLlm: llm);

    [Fact]
    public void Estado_PublicaLosTokensAcumulados()
    {
        var estado = new EstadoServicio();
        estado.RegistrarConsumo(new ConsumoTokens(1500, 250));

        using var doc = JsonDocument.Parse(CrearServidor(estado).ConstruirCuerpo());
        var consumo = doc.RootElement.GetProperty("consumo");

        Assert.Equal(1, consumo.GetProperty("revisionesMedidas").GetInt32());
        Assert.Equal(1750, consumo.GetProperty("tokensAcumulados").GetProperty("total").GetInt32());
        Assert.Equal(1500, consumo.GetProperty("tokensAcumulados").GetProperty("entrada").GetInt32());
    }

    [Fact]
    public void Estado_SinTarifasConfiguradas_NoInventaUnCoste()
    {
        var estado = new EstadoServicio();
        estado.RegistrarConsumo(new ConsumoTokens(1_000_000, 100_000));

        using var doc = JsonDocument.Parse(CrearServidor(estado).ConstruirCuerpo());

        Assert.Equal(
            JsonValueKind.Null,
            doc.RootElement.GetProperty("consumo").GetProperty("costeEstimadoAcumulado").ValueKind);
    }

    [Fact]
    public void Estado_ConTarifasConfiguradas_EstimaElGasto()
    {
        var estado = new EstadoServicio();
        estado.RegistrarConsumo(new ConsumoTokens(1_000_000, 100_000));

        var llm = new ConfiguracionLlm { CostePorMillonEntrada = 3m, CostePorMillonSalida = 15m };

        using var doc = JsonDocument.Parse(CrearServidor(estado, llm).ConstruirCuerpo());

        // 1M * 3 + 0,1M * 15 = 4,5
        Assert.Equal(
            4.5m,
            doc.RootElement.GetProperty("consumo").GetProperty("costeEstimadoAcumulado").GetDecimal());
    }

    [Fact]
    public void ConfiguracionLlm_SinTarifas_PorDefecto()
    {
        // Las tarifas las pone el equipo: inventarlas aquí sería garantizar que envejecen.
        var config = new ConfiguracionLlm();

        Assert.Equal(0m, config.CostePorMillonEntrada);
        Assert.Equal(0m, config.CostePorMillonSalida);
    }

    // ---------- El ejecutor anota el coste ----------

    [Fact]
    public async Task ElEjecutorAnotaElCosteDeCadaRevision()
    {
        var estado = new EstadoServicio();
        var cliente = new ClienteMinimo();
        var decisor = new DecisorRevisar(new RegistradorFalso<DecisorRevisar>());
        decisor.FiltrarPrsParaRevisar(Array.Empty<PullRequest>());

        var sut = new EjecutorVuelta(
            new RegistradorFalso<EjecutorVuelta>(),
            cliente,
            decisor,
            new RevisorConCoste(new ConsumoTokens(900, 120)),
            new AlmacenNulo(),
            new ConfiguracionSondeo { Repositorios = new[] { "equipo-a/repo-1" } },
            ahora: () => DateTimeOffset.UnixEpoch,
            estado: estado);

        await sut.EjecutarAsync(CancellationToken.None);

        Assert.Equal(1020, estado.Capturar().ConsumoAcumulado.Total);
    }

    // ---------- Utilidades ----------

    private static HttpResponseMessage Respuesta(string contenido, int entrada, int salida)
    {
        string json = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = contenido } } },
            usage = new { prompt_tokens = entrada, completion_tokens = salida },
        });

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class RespuestasEnCola : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _respuestas;

        public RespuestasEnCola(params HttpResponseMessage[] respuestas)
            => _respuestas = new Queue<HttpResponseMessage>(respuestas);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_respuestas.Count > 0
                ? _respuestas.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"choices":[{"message":{"content":"{\"hallazgos\":[]}"}}]}""",
                        Encoding.UTF8, "application/json"),
                });
    }

    private sealed class RevisorConCoste : IRevisor
    {
        private readonly ConsumoTokens _consumo;

        public RevisorConCoste(ConsumoTokens consumo) => _consumo = consumo;

        public Task<ResultadoRevision> RevisarAsync(string diff, ContextoRevision? contexto = null, CancellationToken token = default)
            => Task.FromResult(ResultadoRevision.Ok(Array.Empty<Hallazgo>(), _consumo));
    }

    private sealed class ClienteMinimo : IClienteBitbucket
    {
        public Task<IEnumerable<EventoPr>> ListarPrsAbiertos(string repositorio, CancellationToken cancelacion = default)
            => Task.FromResult<IEnumerable<EventoPr>>(new[]
            {
                new EventoPr("equipo-a/repo-1", 1, "c1", "titulo", "main"),
            });

        public Task<string> ObtenerDiff(string repositorio, int numero, CancellationToken cancelacion = default)
            => Task.FromResult("diff --git a/A.cs b/A.cs\n@@ -1,1 +1,1 @@\n+x\n");

        public Task PublicarComentario(string repositorio, int numero, Hallazgo hallazgo, CancellationToken cancelacion = default)
            => Task.CompletedTask;

        public Task PublicarComentarioGeneral(string repositorio, int numero, string texto, CancellationToken cancelacion = default)
            => Task.CompletedTask;
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
