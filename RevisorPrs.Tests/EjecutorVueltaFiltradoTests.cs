using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using RevisorPrs.Servicio;
using Xunit;

namespace RevisorPrs.Tests;

/// <summary>
/// Pruebas de que <see cref="EjecutorVuelta"/> usa de verdad el <see cref="RecortadorDiff"/>
/// y el <see cref="FiltroRuido"/>.
///
/// Ambas piezas estaban implementadas y probadas por su cuenta, pero nadie las llamaba:
/// el diff viajaba entero al modelo (ignorando <c>Bitbucket.TopeBytesDiff</c>) y todo
/// hallazgo se publicaba tal cual (ignorando <c>Llm.SeveridadMinima</c>). Estas pruebas
/// usan las clases reales, no dobles, para que el cableado quede cubierto.
/// </summary>
public class EjecutorVueltaFiltradoTests
{
    /// <summary>Hunk que toca las líneas 10, 11 y 12 del archivo nuevo.</summary>
    private const string DiffConHunk =
        "diff --git a/src/A.cs b/src/A.cs\n" +
        "@@ -10,3 +10,3 @@\n" +
        "-vieja\n" +
        "+nueva 10\n" +
        "+nueva 11\n";

    private static EjecutorVuelta CrearSut(
        ClienteBitbucketFalso cliente,
        IRevisor revisor,
        string severidadMinima,
        int topeBytesDiff)
    {
        var decisor = new DecisorRevisar(new RegistradorFalso<DecisorRevisar>());
        // Consumimos la "primera vuelta" en vacío para que la siguiente sí filtre.
        decisor.FiltrarPrsParaRevisar(Array.Empty<PullRequest>());

        return new EjecutorVuelta(
            new RegistradorFalso<EjecutorVuelta>(),
            cliente,
            decisor,
            revisor,
            new AlmacenFalsoMinimo(),
            new ConfiguracionSondeo { Repositorios = new[] { "test/repo" } },
            ahora: () => DateTimeOffset.UnixEpoch,
            estado: null,
            saneador: null,
            recortador: new RecortadorDiff(new ConfiguracionBitbucket { TopeBytesDiff = topeBytesDiff }),
            filtro: new FiltroRuido(
                Options.Create(new ConfiguracionLlm { SeveridadMinima = severidadMinima }),
                new RegistradorFalso<FiltroRuido>()));
    }

    [Fact]
    public async Task EjecutarAsync_ConHallazgosDeRuido_SoloPublicaLosQueSuperanElFiltro()
    {
        var cliente = new ClienteBitbucketFalso(DiffConHunk);
        cliente.Prs["test/repo"] = new List<EventoPr>
        {
            new("test/repo", 1, "commit-1", "titulo", "rama"),
        };

        var revisor = new RevisorConHallazgos(new[]
        {
            // Se publica: severidad suficiente y línea dentro del hunk.
            new Hallazgo("src/A.cs", 11, "error", "Se publica", "detalle"),
            // Se descarta: severidad por debajo del umbral "media".
            new Hallazgo("src/A.cs", 11, "info", "Ruido por severidad", "detalle"),
            // Se descarta: la línea 900 no la toca el diff.
            new Hallazgo("src/A.cs", 900, "error", "Ruido por línea", "detalle"),
        });

        var sut = CrearSut(cliente, revisor, severidadMinima: "media", topeBytesDiff: 100_000);

        await sut.EjecutarAsync(CancellationToken.None);

        var publicados = cliente.LlamadasPublicarComentario.Select(l => l.hallazgo.Resumen).ToArray();
        Assert.Equal(new[] { "Se publica" }, publicados);
    }

    [Fact]
    public async Task EjecutarAsync_ConDiffQueSuperaElTope_EnviaAlModeloElDiffRecortado()
    {
        // Dos archivos; el tope solo da para el primero.
        string diffGrande =
            "diff --git a/src/Grande.cs b/src/Grande.cs\n" +
            new string('x', 500) + "\n" +
            "diff --git a/src/Segundo.cs b/src/Segundo.cs\n" +
            new string('y', 500) + "\n";

        var cliente = new ClienteBitbucketFalso(diffGrande);
        cliente.Prs["test/repo"] = new List<EventoPr>
        {
            new("test/repo", 1, "commit-1", "titulo", "rama"),
        };

        var revisor = new RevisorConHallazgos(Array.Empty<Hallazgo>());
        var sut = CrearSut(cliente, revisor, severidadMinima: "baja", topeBytesDiff: 600);

        await sut.EjecutarAsync(CancellationToken.None);

        Assert.NotNull(revisor.DiffRecibido);
        Assert.Contains("[RecortadorDiff]", revisor.DiffRecibido!);
        Assert.Contains("src/Segundo.cs", revisor.DiffRecibido!);
        // El contenido del segundo archivo se ha quedado fuera; solo se le nombra.
        Assert.DoesNotContain(new string('y', 500), revisor.DiffRecibido!);
    }

    [Fact]
    public async Task EjecutarAsync_MandaAlModeloElDiffConLasLineasNumeradas()
    {
        // El numerador puede existir y estar probado sin que nadie lo llame: es
        // exactamente lo que pasaba con FiltroRuido y RecortadorDiff. Esta prueba
        // cubre el CABLEADO, no la numeración en sí.
        var cliente = new ClienteBitbucketFalso(
            "diff --git a/src/A.cs b/src/A.cs\n" +
            "--- a/src/A.cs\n" +
            "+++ b/src/A.cs\n" +
            "@@ -10,2 +10,2 @@\n" +
            "+    cambio\n");

        cliente.Prs["test/repo"] = new List<EventoPr>
        {
            new("test/repo", 1, "commit-1", "titulo", "rama"),
        };

        var revisor = new RevisorConHallazgos(Array.Empty<Hallazgo>());
        var sut = CrearSut(cliente, revisor, severidadMinima: "baja", topeBytesDiff: 100_000);

        await sut.EjecutarAsync(CancellationToken.None);

        Assert.NotNull(revisor.DiffRecibido);
        // La línea añadida llega precedida por su número en el archivo nuevo.
        Assert.Contains("   10 +    cambio", revisor.DiffRecibido!, StringComparison.Ordinal);
        // Y las cabeceras siguen intactas para que el modelo sepa de qué archivo habla.
        Assert.Contains("+++ b/src/A.cs", revisor.DiffRecibido!, StringComparison.Ordinal);
    }

    private sealed class ClienteBitbucketFalso : IClienteBitbucket
    {
        private readonly string _diff;

        public ClienteBitbucketFalso(string diff) => _diff = diff;

        public Dictionary<string, List<EventoPr>> Prs { get; } = new();
        public List<(string repositorio, int numero, Hallazgo hallazgo)> LlamadasPublicarComentario { get; } = new();

        public Task<IEnumerable<EventoPr>> ListarPrsAbiertos(string repositorio, CancellationToken cancelacion = default)
            => Task.FromResult(Prs.TryGetValue(repositorio, out var prs)
                ? prs.AsEnumerable()
                : Enumerable.Empty<EventoPr>());

        public Task<string> ObtenerDiff(string repositorio, int numero, CancellationToken cancelacion = default) => Task.FromResult(_diff);

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

    private sealed class RevisorConHallazgos : IRevisor
    {
        private readonly IReadOnlyList<Hallazgo> _hallazgos;

        public RevisorConHallazgos(IReadOnlyList<Hallazgo> hallazgos) => _hallazgos = hallazgos;

        /// <summary>Diff tal y como lo recibió el revisor: ya recortado.</summary>
        public string? DiffRecibido { get; private set; }

        public Task<ResultadoRevision> RevisarAsync(string diff, ContextoRevision? contexto = null, CancellationToken token = default)
        {
            DiffRecibido = diff;
            return Task.FromResult(ResultadoRevision.Ok(_hallazgos));
        }
    }

    private sealed class AlmacenFalsoMinimo : IAlmacen
    {
        private readonly HashSet<(string, int, string)> _revisados = new();

        public void MarcarRevisado(string slugRepo, int idPr, string hashCommit)
            => _revisados.Add((slugRepo, idPr, hashCommit));

        public bool Revisado(string slugRepo, int idPr, string hashCommit)
            => _revisados.Contains((slugRepo, idPr, hashCommit));

        public IEnumerable<(string Repositorio, int Numero, string Commit)> ListarRevisiones()
            => _revisados.Select(t => (t.Item1, t.Item2, t.Item3)).ToList();

        public void MarcarFallido(string slugRepo, int idPr, string hashCommit, string motivo) { }

        public bool DebeReintentar(string slugRepo, int idPr, DateTimeOffset ahora) => true;

        public IEnumerable<(string Repositorio, int PullRequest, string Commit, string Motivo)> ListarFallos()
            => Array.Empty<(string, int, string, string)>();
    
        // --- Idempotencia por comentario (A2) ---
        private readonly HashSet<(string, int, string)> _comentados = new();

        public bool ComentarioPublicado(string slugRepo, int idPr, string huella)
            => _comentados.Contains((slugRepo, idPr, huella));

        public void MarcarComentarioPublicado(string slugRepo, int idPr, string hashCommit, string huella, string comentario)
            => _comentados.Add((slugRepo, idPr, huella));
}
}
