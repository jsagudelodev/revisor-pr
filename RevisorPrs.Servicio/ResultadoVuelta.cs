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
    private readonly ConfiguracionSondeo _configuracion;

    public EjecutorVuelta(ILogger<EjecutorVuelta> logger, ConfiguracionSondeo configuracion)
    {
        _logger = logger;
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

        // RV.1 deja la vuelta como un esqueleto: las acciones reales llegan en RV.2+ (RV.3, RV.4, RV.5).
        // Se publica el resultado para que el siguiente ítem pueda empezar a rellenarlo sin tocar el bucle.
        ResultadoVuelta resultado = new()
        {
            PrsRevisados = 0,
            PrsOmitidos = 0,
            PrsFallidos = 0,
        };

        return Task.CompletedTask;
    }
}
