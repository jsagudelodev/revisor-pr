using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using RevisorPrs.Servicio;
using Xunit;

namespace RevisorPrs.Tests;

/// <summary>
/// Pruebas de que un fallo al descargar el diff NO se confunde con un pull request
/// sin cambios.
///
/// El fallo original: <c>ObtenerDiff</c> devolvía una cadena vacía ante cualquier error
/// de la API. El ejecutor la enviaba al modelo, recibía cero hallazgos, entraba por la
/// rama de éxito y llamaba a <c>MarcarRevisado</c>. Como la clave del almacén es
/// (repo, pr, commit), ese commit no se volvía a revisar NUNCA: un 500 puntual perdía
/// la revisión de forma permanente y silenciosa.
/// </summary>
public class DiffVacioTests
{
    private static readonly EventoPr Pr = new("test/repo", 1, "commit-1", "titulo", "rama");

    private static (EjecutorVuelta Sut, ClienteConDiff Cliente, RevisorEspia Revisor, AlmacenEspia Almacen)
        CrearSut(Func<string> diff)
    {
        var cliente = new ClienteConDiff(diff);
        cliente.Prs["test/repo"] = new List<EventoPr> { Pr };

        var revisor = new RevisorEspia();
        var almacen = new AlmacenEspia();

        var decisor = new DecisorRevisar(new RegistradorFalso<DecisorRevisar>());
        decisor.FiltrarPrsParaRevisar(Array.Empty<PullRequest>()); // consume la primera vuelta

        var sut = new EjecutorVuelta(
            new RegistradorFalso<EjecutorVuelta>(),
            cliente,
            decisor,
            revisor,
            almacen,
            new ConfiguracionSondeo { Repositorios = new[] { "test/repo" } },
            ahora: () => DateTimeOffset.UnixEpoch);

        return (sut, cliente, revisor, almacen);
    }

    [Fact]
    public async Task FalloAlObtenerElDiff_NoMarcaElPrComoRevisado()
    {
        var (sut, _, revisor, almacen) = CrearSut(
            () => throw new HttpRequestException("500 simulado", null, HttpStatusCode.InternalServerError));

        await sut.EjecutarAsync(CancellationToken.None);

        // Lo esencial: el PR NO queda marcado, así que la siguiente vuelta lo reintenta.
        Assert.False(almacen.Revisado(Pr.Repositorio, Pr.Numero, Pr.Commit));
        Assert.Empty(almacen.Revisados);

        // Y queda registrado como fallo, que es lo que activa el backoff.
        var fallo = Assert.Single(almacen.Fallos);
        Assert.Equal(Pr.Repositorio, fallo.Repositorio);
        Assert.Equal(Pr.Numero, fallo.PullRequest);

        // No tiene sentido gastar una llamada al modelo sin diff.
        Assert.False(revisor.Llamado);
    }

    [Fact]
    public async Task FalloAlObtenerElDiff_EnLaVueltaSiguienteSeReintenta()
    {
        // Primera vuelta: la API falla. Segunda vuelta: responde bien.
        var fallaLaPrimera = true;
        var (sut, _, revisor, almacen) = CrearSut(() =>
        {
            if (fallaLaPrimera)
            {
                fallaLaPrimera = false;
                throw new HttpRequestException("500 simulado", null, HttpStatusCode.InternalServerError);
            }
            return "diff --git a/src/A.cs b/src/A.cs\n@@ -1,1 +1,1 @@\n+cambio\n";
        });

        await sut.EjecutarAsync(CancellationToken.None);
        Assert.Empty(almacen.Revisados);

        // El almacén espía no impone backoff, así que la vuelta siguiente lo retoma.
        await sut.EjecutarAsync(CancellationToken.None);

        Assert.True(almacen.Revisado(Pr.Repositorio, Pr.Numero, Pr.Commit));
        Assert.True(revisor.Llamado);
    }

    [Fact]
    public async Task PrSinCambios_SeDaPorRevisadoSinLlamarAlModelo()
    {
        // Un diff vacío que viene de una respuesta correcta sí es un PR sin cambios:
        // no hay nada que comentar y no hay motivo para reintentarlo eternamente.
        var (sut, cliente, revisor, almacen) = CrearSut(() => string.Empty);

        await sut.EjecutarAsync(CancellationToken.None);

        Assert.True(almacen.Revisado(Pr.Repositorio, Pr.Numero, Pr.Commit));
        Assert.Empty(almacen.Fallos);
        Assert.False(revisor.Llamado);
        Assert.Empty(cliente.LlamadasPublicarComentario);
    }

    private sealed class ClienteConDiff : IClienteBitbucket
    {
        private readonly Func<string> _diff;

        public ClienteConDiff(Func<string> diff) => _diff = diff;

        public Dictionary<string, List<EventoPr>> Prs { get; } = new();
        public List<(string repositorio, int numero, Hallazgo hallazgo)> LlamadasPublicarComentario { get; } = new();

        public Task<IEnumerable<EventoPr>> ListarPrsAbiertos(string repositorio, CancellationToken cancelacion = default)
            => Task.FromResult(Prs.TryGetValue(repositorio, out var prs)
                ? prs.AsEnumerable()
                : Enumerable.Empty<EventoPr>());

        public Task<string> ObtenerDiff(string repositorio, int numero, CancellationToken cancelacion = default) => Task.FromResult(_diff());

        public Task PublicarComentario(string repositorio, int numero, Hallazgo hallazgo, CancellationToken cancelacion = default)
        {
            LlamadasPublicarComentario.Add((repositorio, numero, hallazgo));
            return Task.CompletedTask;
        }
    
        /// <summary>Comentarios de resumen publicados (A3).</summary>
        public List<string> Resumenes { get; } = new();

        public Task PublicarComentarioGeneral(string repositorio, int numero, string texto, CancellationToken cancelacion = default)
        {
            Resumenes.Add(texto);
            return Task.CompletedTask;
        }
}

    private sealed class RevisorEspia : IRevisor
    {
        public bool Llamado { get; private set; }

        public Task<ResultadoRevision> RevisarAsync(string diff, ContextoRevision? contexto = null, CancellationToken token = default)
        {
            Llamado = true;
            return Task.FromResult(ResultadoRevision.Ok(Array.Empty<Hallazgo>()));
        }
    }

    private sealed class AlmacenEspia : IAlmacen
    {
        private readonly HashSet<(string, int, string)> _revisados = new();

        public List<(string Repositorio, int Numero, string Commit)> Revisados { get; } = new();
        public List<(string Repositorio, int PullRequest, string Commit, string Motivo)> Fallos { get; } = new();

        public void MarcarRevisado(string slugRepo, int idPr, string hashCommit)
        {
            _revisados.Add((slugRepo, idPr, hashCommit));
            Revisados.Add((slugRepo, idPr, hashCommit));
        }

        public bool Revisado(string slugRepo, int idPr, string hashCommit)
            => _revisados.Contains((slugRepo, idPr, hashCommit));

        public IEnumerable<(string Repositorio, int Numero, string Commit)> ListarRevisiones()
            => _revisados.Select(t => (t.Item1, t.Item2, t.Item3)).ToList();

        public void MarcarFallido(string slugRepo, int idPr, string hashCommit, string motivo)
        {
            Fallos.RemoveAll(f => f.Repositorio == slugRepo && f.PullRequest == idPr);
            Fallos.Add((slugRepo, idPr, hashCommit, motivo));
        }

        // Sin backoff: estos tests miden qué se marca, no cuándo se reintenta.
        public bool DebeReintentar(string slugRepo, int idPr, DateTimeOffset ahora) => true;

        public IEnumerable<(string Repositorio, int PullRequest, string Commit, string Motivo)> ListarFallos()
            => Fallos.ToList();
    
        // --- Idempotencia por comentario (A2) ---
        private readonly HashSet<(string, int, string)> _comentados = new();

        public bool ComentarioPublicado(string slugRepo, int idPr, string huella)
            => _comentados.Contains((slugRepo, idPr, huella));

        public void MarcarComentarioPublicado(string slugRepo, int idPr, string hashCommit, string huella, string comentario)
            => _comentados.Add((slugRepo, idPr, huella));
}
}
