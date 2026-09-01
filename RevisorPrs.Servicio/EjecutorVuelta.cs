using System;
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
    private readonly Func<DateTimeOffset> _ahora;
    private readonly EstadoServicio? _estado;
    private readonly SaneadorSecretos _saneador;

    public EjecutorVuelta(
        ILogger<EjecutorVuelta> logger,
        IClienteBitbucket clienteBitbucket,
        DecisorRevisar decisor,
        IRevisor revisor,
        IAlmacen almacen,
        ConfiguracionSondeo configuracionSondeo,
        Func<DateTimeOffset>? ahora = null,
        EstadoServicio? estado = null,
        SaneadorSecretos? saneador = null)
    {
        _logger = logger;
        _clienteBitbucket = clienteBitbucket;
        _decisor = decisor;
        _revisor = revisor;
        _almacen = almacen;
        _configuracionSondeo = configuracionSondeo;
        _ahora = ahora ?? (() => DateTimeOffset.UtcNow);
        _estado = estado;
        _saneador = saneador ?? SaneadorSecretos.Ninguno;
    }

    public async Task EjecutarAsync(CancellationToken cancelacion)
    {
        _logger.LogInformation("Iniciando vuelta de sondeo.");

        int revisados = 0;
        int omitidos = 0;
        int fallidos = 0;

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

        // Anyadimos los PRs que el almacen tiene en backoff por un fallo
        // reciente: aunque el decisor ya los conozca con el mismo commit, el
        // ejecutor debe decidir si el plazo de reintento ha vencido.
        var prsParaRevisarLista = prsParaRevisar.ToList();
        foreach (var f in _almacen.ListarFallos())
        {
            if (f.Commit is null) continue;
            if (prsParaRevisarLista.Any(p => p.Repositorio == f.Repositorio && p.Numero == f.PullRequest))
            {
                continue;
            }
            prsParaRevisarLista.Add(new PullRequest(f.Repositorio, f.PullRequest, f.Commit));
        }
        prsParaRevisar = prsParaRevisarLista;

        foreach (var pr in prsParaRevisar)
        {
            try
            {
                if (_almacen.Revisado(pr.Repositorio, pr.Numero, pr.Commit))
                {
                    _logger.LogInformation("PR {Repositorio}#{Numero} commit {Commit} ya revisado. Saltando.", pr.Repositorio, pr.Numero, pr.Commit);
                    omitidos++;
                    continue;
                }

                if (!_almacen.DebeReintentar(pr.Repositorio, pr.Numero, _ahora()))
                {
                    _logger.LogInformation(
                        "PR {Repositorio}#{Numero} en backoff por fallos previos. Se omite hasta el próximo reintento.",
                        pr.Repositorio, pr.Numero);
                    omitidos++;
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
                    string motivo = _saneador.Sanear(resultadoRevision.Motivo ?? "revisión sin éxito");
                    _logger.LogWarning("La revisión del PR {Repositorio}#{Numero} no tuvo éxito: {Motivo}", pr.Repositorio, pr.Numero, motivo);
                    _almacen.MarcarFallido(pr.Repositorio, pr.Numero, pr.Commit, motivo);
                    _estado?.RegistrarError($"PR {pr.Repositorio}#{pr.Numero} sin éxito: {motivo}");
                    fallidos++;
                    continue;
                }

                _almacen.MarcarRevisado(pr.Repositorio, pr.Numero, pr.Commit);
                revisados++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error procesando PR {Repositorio}#{Numero}. Se continuará con el siguiente.", pr.Repositorio, pr.Numero);

                // Si la operacion fue cancelada (caida del servicio, RV.17), no
                // dejamos al PR en backoff: queremos que la siguiente vuelta
                // lo reintente sin penalizacion. Tambien respetamos un token
                // cancelado a mitad de una revision exitosa.
                if (ex is OperationCanceledException)
                {
                    if (cancelacion.IsCancellationRequested)
                    {
                        return;
                    }
                    // OperationCanceledException sin cancelacion solicitada:
                    // cliente simulado (tests), seguimos con el siguiente.
                    continue;
                }

                string detalle = _saneador.Sanear(ex.Message);
                _almacen.MarcarFallido(pr.Repositorio, pr.Numero, pr.Commit, detalle);
                _estado?.RegistrarError($"PR {pr.Repositorio}#{pr.Numero} exception: {detalle}");
                fallidos++;
            }
        }

        if (_estado is not null)
        {
            _estado.RegistrarVuelta(new ResultadoVuelta
            {
                PrsRevisados = revisados,
                PrsOmitidos = omitidos,
                PrsFallidos = fallidos,
            });
        }

        _logger.LogInformation("Vuelta de sondeo finalizada.");
    }
}
