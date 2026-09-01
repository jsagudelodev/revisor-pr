using Microsoft.Extensions.Logging;

namespace RevisorPrs.Servicio;

public class EjecutorVuelta : IEjecutorVuelta
{
    private readonly ILogger<EjecutorVuelta> _logger;
    private readonly IClienteBitbucket _clienteBitbucket;
    private readonly DecisorRevisar _decisor;
    private readonly IRevisor _revisor;
    private readonly IAlmacen _almacen;
    private readonly ConfiguracionSondeo _configuracionSondeo;

    public EjecutorVuelta(
        ILogger<EjecutorVuelta> logger,
        IClienteBitbucket clienteBitbucket,
        DecisorRevisar decisor,
        IRevisor revisor,
        IAlmacen almacen,
        ConfiguracionSondeo configuracionSondeo)
    {
        _logger = logger;
        _clienteBitbucket = clienteBitbucket;
        _decisor = decisor;
        _revisor = revisor;
        _almacen = almacen;
        _configuracionSondeo = configuracionSondeo;
    }

    public async Task EjecutarAsync(CancellationToken cancelacion)
    {
        _logger.LogInformation("Iniciando vuelta de sondeo.");

        var todosLosPrs = new List<PullRequest>();

        foreach (var repo in _configuracionSondeo.Repositorios)
        {
            try
            {
                var prs = await _clienteBitbucket.ListarPrsAbiertos(repo);
                todosLosPrs.AddRange(prs.Select(p => new PullRequest(repo, p.Numero, p.Commit)));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al listar PRs del repositorio {Repositorio}. Se continuará con el siguiente.", repo);
            }
        }
        
        var prsParaRevisar = _decisor.FiltrarPrsParaRevisar(todosLosPrs);

        foreach (var pr in prsParaRevisar)
        {
            try
            {
                if (_almacen.Revisado(pr.Repositorio, pr.Numero, pr.Commit))
                {
                    _logger.LogInformation("PR {Repositorio}#{Numero} commit {Commit} ya revisado. Saltando.", pr.Repositorio, pr.Numero, pr.Commit);
                    continue;
                }

                var diff = await _clienteBitbucket.ObtenerDiff(pr.Repositorio, pr.Numero);
                var resultadoRevision = await _revisor.RevisarAsync(diff, cancelacion);

                if (resultadoRevision.Exito)
                {
                    foreach (var hallazgo in resultadoRevision.Hallazgos)
                    {
                        await _clienteBitbucket.PublicarComentario(pr.Repositorio, pr.Numero, hallazgo);
                    }
                }
                else
                {
                    _logger.LogWarning("La revisión del PR {Repositorio}#{Numero} no tuvo éxito: {Motivo}", pr.Repositorio, pr.Numero, resultadoRevision.Motivo);
                }
                
                _almacen.MarcarRevisado(pr.Repositorio, pr.Numero, pr.Commit);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error procesando PR {Repositorio}#{Numero}. Se continuará con el siguiente.", pr.Repositorio, pr.Numero);
            }
        }

        _logger.LogInformation("Vuelta de sondeo finalizada.");
    }
}