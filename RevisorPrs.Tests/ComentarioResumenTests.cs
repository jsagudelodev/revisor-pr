using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RevisorPrs.Servicio;
using Xunit;

namespace RevisorPrs.Tests;

/// <summary>
/// Pruebas del comentario de resumen (A3).
///
/// Un comentario por hallazgo hacía que un pull request con doce hallazgos generara doce
/// notificaciones a cada persona suscrita. Ahora solo los graves se anclan a su línea y
/// el resto llega junto en un único comentario.
/// </summary>
public class ComentarioResumenTests
{
    private static readonly EventoPr Pr = new("equipo-a/repo-1", 7, "commit-1", "titulo", "rama");

    private static Hallazgo H(string archivo, int? linea, string severidad, string resumen) =>
        new(archivo, linea, severidad, resumen, "detalle de " + resumen);

    // ---------- Reparto en el ejecutor ----------

    [Fact]
    public async Task DoceHallazgos_GeneranTresNotificaciones_NoDoce()
    {
        var hallazgos = new List<Hallazgo>();
        for (int i = 1; i <= 2; i++) hallazgos.Add(H("src/A.cs", i, "error", "error " + i));
        for (int i = 1; i <= 6; i++) hallazgos.Add(H("src/B.cs", i, "warning", "aviso " + i));
        for (int i = 1; i <= 4; i++) hallazgos.Add(H("src/C.cs", i, "info", "nota " + i));

        var cliente = await EjecutarCon(hallazgos, severidadAnclada: "error");

        // 2 anclados (los Error) + 1 resumen = 3 notificaciones en vez de 12.
        Assert.Equal(2, cliente.Anclados.Count);
        Assert.Single(cliente.Resumenes);
        Assert.Equal(new[] { "error 1", "error 2" }, cliente.Anclados.Select(h => h.Resumen).ToArray());
    }

    [Fact]
    public async Task ElResumenListaLosQueNoSeAnclaron_ConSuDetalle()
    {
        var hallazgos = new List<Hallazgo>
        {
            H("src/A.cs", 1, "error", "grave"),
            H("src/B.cs", 2, "warning", "menos grave"),
        };

        var cliente = await EjecutarCon(hallazgos, severidadAnclada: "error");

        string resumen = Assert.Single(cliente.Resumenes);
        Assert.Contains("menos grave", resumen, StringComparison.Ordinal);
        Assert.Contains("detalle de menos grave", resumen, StringComparison.Ordinal);
        Assert.Contains("`src/B.cs:2`", resumen, StringComparison.Ordinal);

        // El que va anclado no se repite dentro del resumen.
        Assert.DoesNotContain("src/A.cs", resumen, StringComparison.Ordinal);
        Assert.DoesNotContain("detalle de grave", resumen, StringComparison.Ordinal);
        Assert.Contains("Hay además 1 hallazgo comentado en su línea.", resumen, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SinHallazgosParaResumir_NoSePublicaResumen()
    {
        // Un pull request cuyos hallazgos van todos anclados no recibe comentario extra.
        var hallazgos = new List<Hallazgo> { H("src/A.cs", 1, "error", "grave") };

        var cliente = await EjecutarCon(hallazgos, severidadAnclada: "error");

        Assert.Single(cliente.Anclados);
        Assert.Empty(cliente.Resumenes);
    }

    [Fact]
    public async Task PullRequestLimpio_NoRecibeNingunComentario()
    {
        var cliente = await EjecutarCon(new List<Hallazgo>(), severidadAnclada: "error");

        Assert.Empty(cliente.Anclados);
        Assert.Empty(cliente.Resumenes);
    }

    [Fact]
    public async Task HallazgoGraveSinLinea_VaAlResumen_PorqueNoHayDondeAnclarlo()
    {
        var hallazgos = new List<Hallazgo> { H("src/A.cs", null, "error", "general") };

        var cliente = await EjecutarCon(hallazgos, severidadAnclada: "error");

        Assert.Empty(cliente.Anclados);
        Assert.Single(cliente.Resumenes);
    }

    [Fact]
    public async Task ConUmbralBajo_SeAnclaTodo_ComoAntesDelResumen()
    {
        var hallazgos = new List<Hallazgo>
        {
            H("src/A.cs", 1, "error", "grave"),
            H("src/B.cs", 2, "info", "menor"),
        };

        var cliente = await EjecutarCon(hallazgos, severidadAnclada: "baja");

        Assert.Equal(2, cliente.Anclados.Count);
        Assert.Empty(cliente.Resumenes);
    }

    [Fact]
    public async Task LosDelResumenTampocoSeRepitenEnLaVueltaSiguiente()
    {
        var hallazgos = new List<Hallazgo>
        {
            H("src/A.cs", 1, "error", "grave"),
            H("src/B.cs", 2, "warning", "menos grave"),
        };

        var cliente = new ClienteEspia();
        cliente.Prs["equipo-a/repo-1"] = new List<EventoPr> { Pr };
        var almacen = new AlmacenEnMemoria();
        var sut = Crear(cliente, almacen, hallazgos, "error");

        await sut.EjecutarAsync(CancellationToken.None);
        await sut.EjecutarAsync(CancellationToken.None);

        Assert.Single(cliente.Anclados);
        Assert.Single(cliente.Resumenes);
    }

    [Fact]
    public async Task SinConfigurarNada_SoloSeAnclanLosErrores()
    {
        // La promesa es que funcione bien recién instalado. Los demás tests fijan
        // SeveridadAnclada a mano, así que sin esta prueba el valor POR DEFECTO —que es
        // el que va a usar casi todo el mundo— no estaría cubierto por nada.
        var hallazgos = new List<Hallazgo>
        {
            H("src/A.cs", 1, "error", "grave"),
            H("src/B.cs", 2, "warning", "menos grave"),
            H("src/C.cs", 3, "info", "menor"),
        };

        var cliente = new ClienteEspia();
        cliente.Prs["equipo-a/repo-1"] = new List<EventoPr> { Pr };

        var decisor = new DecisorRevisar(new RegistradorFalso<DecisorRevisar>());
        decisor.FiltrarPrsParaRevisar(Array.Empty<PullRequest>());

        var sut = new EjecutorVuelta(
            new RegistradorFalso<EjecutorVuelta>(),
            cliente,
            decisor,
            new RevisorFijo(hallazgos),
            new AlmacenEnMemoria(),
            new ConfiguracionSondeo { Repositorios = new[] { "equipo-a/repo-1" } },
            ahora: () => DateTimeOffset.UnixEpoch,
            // Configuración recién sacada de la caja, sin tocar SeveridadAnclada.
            configuracionLlm: new ConfiguracionLlm());

        await sut.EjecutarAsync(CancellationToken.None);

        Assert.Equal(new[] { "grave" }, cliente.Anclados.Select(h => h.Resumen).ToArray());
        Assert.Single(cliente.Resumenes);
    }

    // ---------- Texto del resumen ----------

    [Fact]
    public void ComponerResumen_CuentaPorSeveridadDeMayorAMenor()
    {
        var enResumen = new List<Hallazgo>
        {
            H("a.cs", 1, "info", "n1"),
            H("a.cs", 2, "warning", "a1"),
            H("a.cs", 3, "warning", "a2"),
            H("a.cs", 4, "info", "n2"),
        };

        string texto = FormateadorComentario.ComponerResumen(enResumen, anclados: 0);

        Assert.StartsWith("**Revisión automática** · 2 avisos y 2 notas", texto, StringComparison.Ordinal);
    }

    [Fact]
    public void ComponerResumen_ConUnSoloHallazgo_UsaSingular()
    {
        var enResumen = new List<Hallazgo> { H("a.cs", 1, "warning", "uno") };

        string texto = FormateadorComentario.ComponerResumen(enResumen, anclados: 0);

        Assert.StartsWith("**Revisión automática** · 1 aviso\n", texto, StringComparison.Ordinal);
    }

    [Fact]
    public void ComponerResumen_MencionaLosAncladosSoloSiLosHay()
    {
        var enResumen = new List<Hallazgo> { H("a.cs", 1, "warning", "uno") };

        Assert.Contains(
            "Hay además 3 hallazgos comentados en sus líneas.",
            FormateadorComentario.ComponerResumen(enResumen, anclados: 3),
            StringComparison.Ordinal);

        Assert.Contains(
            "Hay además 1 hallazgo comentado en su línea.",
            FormateadorComentario.ComponerResumen(enResumen, anclados: 1),
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "Hay además",
            FormateadorComentario.ComponerResumen(enResumen, anclados: 0),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ComponerResumen_SinHallazgos_Lanza()
    {
        // Un resumen vacío no debería llegar a componerse: el ejecutor no lo publica.
        Assert.Throws<ArgumentException>(
            () => FormateadorComentario.ComponerResumen(Array.Empty<Hallazgo>(), anclados: 0));
    }

    // ---------- Utilidades ----------

    private static async Task<ClienteEspia> EjecutarCon(List<Hallazgo> hallazgos, string severidadAnclada)
    {
        var cliente = new ClienteEspia();
        cliente.Prs["equipo-a/repo-1"] = new List<EventoPr> { Pr };

        await Crear(cliente, new AlmacenEnMemoria(), hallazgos, severidadAnclada)
            .EjecutarAsync(CancellationToken.None);

        return cliente;
    }

    private static EjecutorVuelta Crear(
        IClienteBitbucket cliente,
        IAlmacen almacen,
        List<Hallazgo> hallazgos,
        string severidadAnclada)
    {
        var decisor = new DecisorRevisar(new RegistradorFalso<DecisorRevisar>());
        decisor.FiltrarPrsParaRevisar(Array.Empty<PullRequest>()); // consume la primera vuelta

        return new EjecutorVuelta(
            new RegistradorFalso<EjecutorVuelta>(),
            cliente,
            decisor,
            new RevisorFijo(hallazgos),
            almacen,
            new ConfiguracionSondeo { Repositorios = new[] { "equipo-a/repo-1" } },
            ahora: () => DateTimeOffset.UnixEpoch,
            configuracionLlm: new ConfiguracionLlm { SeveridadAnclada = severidadAnclada });
    }

    private sealed class ClienteEspia : IClienteBitbucket
    {
        public Dictionary<string, List<EventoPr>> Prs { get; } = new();
        public List<Hallazgo> Anclados { get; } = new();
        public List<string> Resumenes { get; } = new();

        public Task<IEnumerable<EventoPr>> ListarPrsAbiertos(string repositorio, CancellationToken cancelacion = default)
            => Task.FromResult(Prs.TryGetValue(repositorio, out var p) ? p.AsEnumerable() : Enumerable.Empty<EventoPr>());

        public Task<string> ObtenerDiff(string repositorio, int numero, CancellationToken cancelacion = default)
            => Task.FromResult("diff --git a/A.cs b/A.cs\n@@ -1,20 +1,20 @@\n+cambio\n");

        public Task PublicarComentario(string repositorio, int numero, Hallazgo hallazgo, CancellationToken cancelacion = default)
        {
            Anclados.Add(hallazgo);
            return Task.CompletedTask;
        }

        public Task PublicarComentarioGeneral(string repositorio, int numero, string texto, CancellationToken cancelacion = default)
        {
            Resumenes.Add(texto);
            return Task.CompletedTask;
        }
    }

    private sealed class RevisorFijo : IRevisor
    {
        private readonly IReadOnlyList<Hallazgo> _hallazgos;

        public RevisorFijo(IReadOnlyList<Hallazgo> hallazgos) => _hallazgos = hallazgos;

        public Task<ResultadoRevision> RevisarAsync(string diff, ContextoRevision? contexto = null, CancellationToken token = default)
            => Task.FromResult(ResultadoRevision.Ok(_hallazgos));
    }

    private sealed class AlmacenEnMemoria : IAlmacen
    {
        private readonly HashSet<(string, int, string)> _revisados = new();
        private readonly HashSet<(string, int, string)> _comentados = new();

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

        public bool ComentarioPublicado(string slugRepo, int idPr, string huella)
            => _comentados.Contains((slugRepo, idPr, huella));

        public void MarcarComentarioPublicado(string slugRepo, int idPr, string hashCommit, string huella, string comentario)
            => _comentados.Add((slugRepo, idPr, huella));
    }
}
