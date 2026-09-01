using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace RevisorPrs.Servicio;

public record PullRequest(string Repositorio, int Numero, string Commit);

/// <summary>
/// Decide qué pull requests hay que revisar a partir de la lista de abiertos.
/// La idempotencia (qué ya fue revisado) la lleva el <see cref="IAlmacen"/>,
/// no este decisor: aquí solo se deduplican los PRs que aparecen varias veces
/// en la lista de abiertos y se devuelven todos los que hay que mirar.
/// </summary>
public class DecisorRevisar
{
    private readonly ILogger<DecisorRevisar> _logger;

    public DecisorRevisar(ILogger<DecisorRevisar> logger)
    {
        _logger = logger;
    }

    public IEnumerable<PullRequest> FiltrarPrsParaRevisar(IEnumerable<PullRequest> prsAbiertos)
    {
        var prsDeduplicados = prsAbiertos
            .GroupBy(pr => (pr.Repositorio, pr.Numero))
            .Select(g => g.OrderBy(pr => pr.Commit).Last())
            .ToList();

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Decisor: {Cantidad} PR(s) abierto(s) únicos a evaluar.", prsDeduplicados.Count);
        }

        return prsDeduplicados;
    }
}
