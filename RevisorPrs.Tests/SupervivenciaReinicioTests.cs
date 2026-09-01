using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RevisorPrs.Servicio;
using Xunit;

namespace RevisorPrs.Tests;

/// <summary>
/// Pruebas de supervivencia al reinicio del servicio (RV.17).
///
/// El servicio se cae a mitad de una vuelta. Al volver a arrancar, NO debe
/// volver a comentar lo que ya habia comentado, y SI debe retomar los
/// pull requests que se quedaron sin procesar.
/// </summary>
public class SupervivenciaReinicioTests
{
    private static string CrearBaseTemporal()
    {
        string carpeta = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(carpeta);
        return Path.Combine(carpeta, "supervivencia.db");
    }

    private static EjecutorVuelta CrearEjecutor(
        ILogger<EjecutorVuelta> logger,
        IClienteBitbucket cliente,
        DecisorRevisar decisor,
        IAlmacen almacen,
        string[] repositorios)
        => new(
            logger,
            cliente,
            decisor,
            new RevisorFalsoPublicaHallazgo(),
            almacen,
            new ConfiguracionSondeo { Repositorios = repositorios });

    [Fact]
    public async Task VueltaInterrumpida_AlReanudar_NoRecomientaYRetoma()
    {
        // Arrange: una sola base de datos SQLite que sobrevive a las dos "instancias".
        string rutaBase = CrearBaseTemporal();

        var pr1 = new EventoPr("equipo-a/repo-1", 1, "commit-1", "titulo-1", "rama-1");
        var pr2 = new EventoPr("equipo-a/repo-1", 2, "commit-2", "titulo-2", "rama-2");

        // ---- PRIMERA INSTANCIA (vuelta interrumpida) ----
        // La primera instancia arranca "de cero": la logica RV.14b del ejecutor
        // ya no depende de que el decisor descarte en la primera vuelta, pero el
        // decisor la consume para tener el listado de PRs conocidos en memoria.
        // El decisor de la primera instancia recibe el almacén para que la
        // primera vuelta (sin estado previo) persista los PRs abiertos como
        // ya revisados. Así, tras un reinicio, el decisor reconstruido no los
        // trata como histórico y sabe que ya fueron procesados.
        var clientePrimera = new ClienteBitbucketCanceladorEn(numero: 2);
        clientePrimera.Prs["equipo-a/repo-1"] = new List<EventoPr> { pr1, pr2 };

        using (var almacen = new Almacen(rutaBase))
        {
            var decisorPrimera = new DecisorRevisar(
                new RegistradorFalso<DecisorRevisar>(),
                almacen);
            var sut = CrearEjecutor(
                new RegistradorFalso<EjecutorVuelta>(),
                clientePrimera,
                decisorPrimera,
                almacen,
                new[] { "equipo-a/repo-1" });

            await sut.EjecutarAsync(CancellationToken.None);
        }

        // Tras la interrupcion: el PR 1 quedo publicado y marcado como revisado.
        // El PR 2 NO se llego a publicar (la cancelacion lo freno antes de publicar).
        Assert.Single(clientePrimera.LlamadasPublicarComentario);
        Assert.Equal(1, clientePrimera.LlamadasPublicarComentario.First().numero);

        // ---- SEGUNDA INSTANCIA (misma base, nuevo Almacen, nuevo Decisor) ----
        // Replica el reinicio del servicio: el DecisorRevisar se vuelve a
        // construir y SI tiene acceso al almacen, debe rehidratar su estado
        // desde SQLite en vez de volver a la "primera vuelta" ciega.
        var clienteSegunda = new ClienteBitbucketFalso();
        clienteSegunda.Prs["equipo-a/repo-1"] = new List<EventoPr> { pr1, pr2 };

        using (var almacen = new Almacen(rutaBase))
        {
            var decisorSegunda = new DecisorRevisar(
                new RegistradorFalso<DecisorRevisar>(),
                almacen);

            var sut = CrearEjecutor(
                new RegistradorFalso<EjecutorVuelta>(),
                clienteSegunda,
                decisorSegunda,
                almacen,
                new[] { "equipo-a/repo-1" });

            await sut.EjecutarAsync(CancellationToken.None);
        }

        // Assert: la cuenta de publicaciones DEMUESTRA que el PR 1 NO se volvio a
        // comentar (su (repo, pr, commit) ya estaba en la base) y que el PR 2 SI
        // se proceso. En la segunda instancia solo se publica el PR 2 una vez.
        Assert.Single(clienteSegunda.LlamadasPublicarComentario);
        Assert.DoesNotContain(
            clienteSegunda.LlamadasPublicarComentario,
            c => c.numero == 1);
        Assert.Single(
            clienteSegunda.LlamadasPublicarComentario.Where(c => c.numero == 2));

        // Tambien verificamos contra la base: el PR 1 esta marcado desde la primera
        // instancia, y el PR 2 desde la segunda.
        using (var conexion = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={rutaBase}"))
        {
            conexion.Open();
            using var cmd = conexion.CreateCommand();
            cmd.CommandText = @"SELECT PullRequest FROM Revisiones ORDER BY PullRequest";
            using var lector = cmd.ExecuteReader();
            var marcados = new List<int>();
            while (lector.Read())
            {
                marcados.Add(lector.GetInt32(0));
            }
            Assert.Equal(new[] { 1, 2 }, marcados);
        }
    }

    /// <summary>
    /// Cliente falso que publica los PRs por orden y cancela la operacion justo
    /// cuando le llega el PR indicado (<paramref name="numero"/>). Reproduce
    /// "el servicio se para cuando va por el segundo PR".
    /// </summary>
    private sealed class ClienteBitbucketCanceladorEn : IClienteBitbucket
    {
        public Dictionary<string, List<EventoPr>> Prs { get; } = new();
        public int CancelarEn { get; set; }
        public List<(string repositorio, int numero)> LlamadasObtenerDiff { get; } = new();
        public List<(string repositorio, int numero, Hallazgo hallazgo)> LlamadasPublicarComentario { get; } = new();

        private bool _diffDelObjetivoHecho;

        public ClienteBitbucketCanceladorEn(int numero)
        {
            CancelarEn = numero;
        }

        public Task<IEnumerable<EventoPr>> ListarPrsAbiertos(string repositorio)
        {
            if (Prs.TryGetValue(repositorio, out var prs))
            {
                return Task.FromResult(prs.AsEnumerable());
            }
            return Task.FromResult(Enumerable.Empty<EventoPr>());
        }

        public Task<string> ObtenerDiff(string repositorio, int numero)
        {
            LlamadasObtenerDiff.Add((repositorio, numero));
            if (numero == CancelarEn && !_diffDelObjetivoHecho)
            {
                _diffDelObjetivoHecho = true;
                throw new OperationCanceledException("Reinicio simulado del servicio.");
            }
            return Task.FromResult("diff");
        }

        public Task PublicarComentario(string repositorio, int numero, Hallazgo hallazgo)
        {
            LlamadasPublicarComentario.Add((repositorio, numero, hallazgo));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Cliente falso de la segunda vuelta: no cancela nada y solo cuenta llamadas.
    /// </summary>
    private sealed class ClienteBitbucketFalso : IClienteBitbucket
    {
        public Dictionary<string, List<EventoPr>> Prs { get; } = new();
        public List<(string repositorio, int numero)> LlamadasObtenerDiff { get; } = new();
        public List<(string repositorio, int numero, Hallazgo hallazgo)> LlamadasPublicarComentario { get; } = new();

        public Task<IEnumerable<EventoPr>> ListarPrsAbiertos(string repositorio)
        {
            if (Prs.TryGetValue(repositorio, out var prs))
            {
                return Task.FromResult(prs.AsEnumerable());
            }
            return Task.FromResult(Enumerable.Empty<EventoPr>());
        }

        public Task<string> ObtenerDiff(string repositorio, int numero)
        {
            LlamadasObtenerDiff.Add((repositorio, numero));
            return Task.FromResult("diff");
        }

        public Task PublicarComentario(string repositorio, int numero, Hallazgo hallazgo)
        {
            LlamadasPublicarComentario.Add((repositorio, numero, hallazgo));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Revisor minimo: devuelve siempre un hallazgo para que la publicacion ocurra.
    /// </summary>
    private sealed class RevisorFalsoPublicaHallazgo : IRevisor
    {
        public Task<ResultadoRevision> RevisarAsync(string diff, CancellationToken token = default)
        {
            return Task.FromResult(ResultadoRevision.Ok(new List<Hallazgo>
            {
                new Hallazgo("archivo.cs", 1, "info", "resumen", "detalle")
            }));
        }
    }
}
