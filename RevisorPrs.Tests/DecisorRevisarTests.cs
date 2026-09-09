using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Xunit;
using RevisorPrs.Servicio;

namespace RevisorPrs.Tests;

public class DecisorRevisarTests
{
    private readonly ILogger<DecisorRevisar> _logger = new LoggerFactory().CreateLogger<DecisorRevisar>();

    [Fact]
    public void PrimeraVueltaNoDevuelvePrsParaRevisar()
    {
        var decisor = new DecisorRevisar(_logger);

        var prsAbiertos = new List<PullRequest>
        {
            new PullRequest("repo/uno", 1, "abc123"),
            new PullRequest("repo/uno", 2, "def456"),
        };

        var prsParaRevisar = decisor.FiltrarPrsParaRevisar(prsAbiertos).ToList();

        Assert.Empty(prsParaRevisar);
    }

    [Fact]
    public void PrNuevoSeRevisa()
    {
        var decisor = new DecisorRevisar(_logger);

        var prsPrimerVuelta = new List<PullRequest>
        {
            new PullRequest("repo/uno", 1, "abc123"),
        };
        _ = decisor.FiltrarPrsParaRevisar(prsPrimerVuelta).ToList();

        var prsNuevos = new List<PullRequest>
        {
            new PullRequest("repo/uno", 1, "abc123"),
            new PullRequest("repo/uno", 2, "def456"),
        };

        var prsParaRevisar = decisor.FiltrarPrsParaRevisar(prsNuevos).ToList();

        Assert.Single(prsParaRevisar);
        Assert.Equal(2, prsParaRevisar[0].Numero);
    }

    [Fact]
    public void PrYaRevisadoSinCambiosNoSeRevisa()
    {
        var decisor = new DecisorRevisar(_logger);

        var prsIniciales = new List<PullRequest>
        {
            new PullRequest("repo/uno", 1, "abc123"),
            new PullRequest("repo/uno", 2, "def456"),
        };
        _ = decisor.FiltrarPrsParaRevisar(prsIniciales).ToList();

        var prsSinCambios = new List<PullRequest>
        {
            new PullRequest("repo/uno", 1, "abc123"),
            new PullRequest("repo/uno", 2, "def456"),
        };

        var prsParaRevisar = decisor.FiltrarPrsParaRevisar(prsSinCambios).ToList();

        Assert.Empty(prsParaRevisar);
    }

    [Fact]
    public void PrConCommitNuevoSobreRevisionPreviaSeRevisa()
    {
        var decisor = new DecisorRevisar(_logger);

        var prsIniciales = new List<PullRequest>
        {
            new PullRequest("repo/uno", 1, "abc123"),
        };
        _ = decisor.FiltrarPrsParaRevisar(prsIniciales).ToList();

        var prsActualizados = new List<PullRequest>
        {
            new PullRequest("repo/uno", 2, "def456"),
            new PullRequest("repo/uno", 1, "nuevoCommit"),
        };

        var prsParaRevisar = decisor.FiltrarPrsParaRevisar(prsActualizados).ToList();

        // Hasta D1 este test exigía que SOLO se devolviera el PR con commit nuevo, y que
        // el PR 2 —completamente nuevo— esperase a la vuelta siguiente. Se cambió a
        // propósito: ese aplazamiento no evitaba ningún problema (las guardas de
        // idempotencia del almacén ya impiden revisar dos veces) y hacía esperar un
        // intervalo entero sin motivo.
        Assert.Equal(2, prsParaRevisar.Count);

        var actualizado = Assert.Single(prsParaRevisar.Where(p => p.Numero == 1));
        Assert.Equal("nuevoCommit", actualizado.Commit);

        Assert.Contains(prsParaRevisar, p => p.Numero == 2);
    }
}
