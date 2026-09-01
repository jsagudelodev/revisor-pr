using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace RevisorPrs.Servicio;

public record PullRequest(string Repositorio, int Numero, string Commit);

public class DecisorRevisar
{
    private readonly ILogger<DecisorRevisar> _logger;
    private readonly Dictionary<(string Repositorio, int Numero), string> _prsRevisados = new();
    private bool _primeraVuelta = true;

    public DecisorRevisar(ILogger<DecisorRevisar> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Dada la lista de PRs abiertos, devuelve solo los que hay que revisar.
    /// La primera vez sobre un repositorio nuevo no analiza el histórico, solo lo actual abierto.
    /// </summary>
    public IEnumerable<PullRequest> FiltrarPrsParaRevisar(IEnumerable<PullRequest> prsAbiertos)
    {
        var prsAgrupados = prsAbiertos
            .GroupBy(pr => (pr.Repositorio, pr.Numero))
            .Select(g => g.OrderBy(pr => pr.Commit).Last())
            .ToList();

        if (_primeraVuelta)
        {
            _primeraVuelta = false;
            foreach (var pr in prsAgrupados)
            {
                _prsRevisados[(pr.Repositorio, pr.Numero)] = pr.Commit;
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation($"Primera vuelta: no se revisan PRs abiertos existentes.");
            }

            return Enumerable.Empty<PullRequest>();
        }

        var prsParaRevisar = new List<PullRequest>();
        foreach (var pr in prsAgrupados)
        {
            var key = (pr.Repositorio, pr.Numero);
            if (!_prsRevisados.ContainsKey(key) || _prsRevisados[key] != pr.Commit)
            {
                prsParaRevisar.Add(pr);
            }
        }

        // Si hay PRs con nuevos commits, esos son los únicos que se procesan en esta vuelta.
        var prsActualizados = prsParaRevisar
            .Where(p => _prsRevisados.ContainsKey((p.Repositorio, p.Numero)))
            .ToList();

        if (prsActualizados.Any())
        {
            prsParaRevisar = prsActualizados;
        }

        foreach (var pr in prsParaRevisar)
        {
            _prsRevisados[(pr.Repositorio, pr.Numero)] = pr.Commit;
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation($"Se revisarán {prsParaRevisar.Count} PR(s) nuevos o con commits no revisados.");
        }

        return prsParaRevisar;
    }
}
