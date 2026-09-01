using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace RevisorPrs.Servicio;

public record PullRequest(string Repositorio, int Numero, string Commit);

public class DecisorRevisar
{
    private readonly ILogger<DecisorRevisar> _logger;
    private readonly IAlmacen? _almacen;
    private readonly Dictionary<(string Repositorio, int Numero), string> _prsRevisados = new();
    private bool _primeraVuelta = true;

    public DecisorRevisar(ILogger<DecisorRevisar> logger, IAlmacen? almacen = null)
    {
        _logger = logger;
        _almacen = almacen;

        // Si el servicio se esta rearmando, el almacen ya sabe que PRs se
        // comentaron: lo usamos como fuente de verdad para no perder el estado
        // al reiniciar (RV.17).
        if (_almacen is not null)
        {
            foreach (var (repositorio, numero, commit) in _almacen.ListarRevisiones())
            {
                _prsRevisados[(repositorio, numero)] = commit;
            }

            if (_prsRevisados.Count > 0)
            {
                _primeraVuelta = false;
            }
        }
    }

    /// <summary>
    /// Dada la lista de PRs abiertos, devuelve solo los que hay que revisar.
    /// En la primera vuelta sobre un repositorio nuevo NO se revisa el
    /// histórico: los PRs abiertos se memorizan como ya vistos y no se
    /// devuelven para revisión. A partir de la segunda vuelta, se devuelven
    /// los PRs nuevos o con un commit distinto al último visto. Esto aplica
    /// tanto en memoria como tras un reinicio rehidratado desde el almacén:
    /// si el almacén ya tiene revisiones, el decisor NO está en "primera
    /// vuelta" y filtra directamente.
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

            if (_almacen is null)
            {
                // Sin almacén no podemos persistir: si devolviéramos los PRs
                // para revisión, un reinicio del servicio los reprocesaría y
                // rompería la idempotencia. Por tanto, en la primera vuelta
                // sin almacén NO se revisa el histórico: se memoriza en
                // memoria y no se devuelve nada. Las siguientes vueltas (en
                // el mismo proceso) sí filtran correctamente.
                foreach (var pr in prsAgrupados)
                {
                    _prsRevisados[(pr.Repositorio, pr.Numero)] = pr.Commit;
                }

                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation($"Primera vuelta sin almacén: se memorizan {prsAgrupados.Count} PR(s) abiertos sin revisarlos.");
                }

                return Array.Empty<PullRequest>();
            }

            // Con almacén sí podemos persistir: dejamos que el ejecutor
            // procese los PRs abiertos y los marque al terminar (idempotencia
            // por almacén, RV.14b). Si el servicio cae a mitad, solo quedan
            // marcados los PRs cuyo procesamiento completó, y un reinicio
            // rehidrata ese estado para no reprocesarlos. Memorizamos en
            // memoria para que esta misma vuelta no los devuelva duplicados.
            foreach (var pr in prsAgrupados)
            {
                _prsRevisados[(pr.Repositorio, pr.Numero)] = pr.Commit;
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation($"Primera vuelta con almacén: se procesan {prsAgrupados.Count} PR(s) abiertos por primera y única vez.");
            }

            return prsAgrupados;
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

        // Si en esta vuelta hay PRs con un commit nuevo sobre uno ya visto,
        // esos son los únicos que se revisan: un PR totalmente nuevo no debe
        // mezclarse con un re-análisis en la misma vuelta (RV.14b).
        var prsActualizados = prsParaRevisar
            .Where(p => _prsRevisados.ContainsKey((p.Repositorio, p.Numero)))
            .ToList();

        if (prsActualizados.Count > 0)
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
