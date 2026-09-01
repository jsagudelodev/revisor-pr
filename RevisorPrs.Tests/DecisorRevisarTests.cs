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

        Assert.Single(prsParaRevisar);
        Assert.Equal(1, prsParaRevisar[0].Numero);
        Assert.Equal("nuevoCommit", prsParaRevisar[0].Commit);
    }
}
