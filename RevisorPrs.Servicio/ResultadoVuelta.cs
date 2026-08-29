namespace RevisorPrs.Servicio;

public class ResultadoVuelta
{
    public int PrsRevisados { get; init; }
    public int PrsOmitidos { get; init; }
    public int PrsFallidos { get; init; }
}

public class EjecutorVuelta : IEjecutorVuelta
{
    private readonly ILogger<EjecutorVuelta> _logger;
    private readonly ILogger<DecisorRevisar> _loggerDecisor;
    private readonly ConfiguracionSondeo _configuracion;

    public EjecutorVuelta(ILogger<EjecutorVuelta> logger, ILogger<DecisorRevisar> loggerDecisor, ConfiguracionSondeo configuracion)
    {
        _logger = logger;
        _loggerDecisor = loggerDecisor;
        _configuracion = configuracion;
    }

    public Task EjecutarAsync(CancellationToken cancelacion)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Vuelta de sondeo sobre {Cantidad} repositorio(s).",
                _configuracion.Repositorios.Length);
        }

        var decisor = new DecisorRevisar(new LoggerFactory().CreateLogger<DecisorRevisar>());

        // Ejemplo simulado de PRs abiertos para esta vuelta
        var prsAbiertos = new List<PullRequest>
        {
            new PullRequest("repo/uno", 1, "commit123"),
            new PullRequest("repo/uno", 2, "commit456"),
            new PullRequest("repo/dos", 1, "commit789"),
        };

        var prsParaRevisar = decisor.FiltrarPrsParaRevisar(prsAbiertos).ToList();

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation($"Se procesan {prsParaRevisar.Count} PR(s) para revisión.");
        }

        ResultadoVuelta resultado = new()
        {
            PrsRevisados = prsParaRevisar.Count,
            PrsOmitidos = prsAbiertos.Count - prsParaRevisar.Count,
            PrsFallidos = 0,
        };

        return Task.CompletedTask;
    }
}
