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
    public void DevuelveTodosLosPrsAbiertosAlEvaluarlos()
    {
        var decisor = new DecisorRevisar(_logger);

        var prsAbiertos = new List<PullRequest>
        {
            new PullRequest("repo/uno", 1, "abc123"),
            new PullRequest("repo/uno", 2, "def456"),
        };

        var prsParaRevisar = decisor.FiltrarPrsParaRevisar(prsAbiertos).ToList();

        Assert.Equal(2, prsParaRevisar.Count);
    }

    [Fact]
    public void DedupicaPorRepositorioYNumeroQuedandoseConElUltimoCommit()
    {
        var decisor = new DecisorRevisar(_logger);

        var prsAbiertos = new List<PullRequest>
        {
            new PullRequest("repo/uno", 1, "antiguo"),
            new PullRequest("repo/uno", 1, "nuevo"),
            new PullRequest("repo/uno", 1, "intermedio"),
        };

        var prsParaRevisar = decisor.FiltrarPrsParaRevisar(prsAbiertos).ToList();

        var unico = Assert.Single(prsParaRevisar);
        Assert.Equal("nuevo", unico.Commit);
    }
}
