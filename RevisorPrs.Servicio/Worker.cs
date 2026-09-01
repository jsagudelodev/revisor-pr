namespace RevisorPrs.Servicio;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly ConfiguracionSondeo _configuracion;
    private readonly IEjecutorVuelta _ejecutor;
    private readonly IReloj _reloj;
    private readonly SemaphoreSlim _candadoVuelta = new SemaphoreSlim(1);

    public Worker(
        ILogger<Worker> logger,
        ConfiguracionSondeo configuracion,
        IEjecutorVuelta ejecutor,
        IReloj reloj)
    {
        _logger = logger;
        _configuracion = configuracion;
        _ejecutor = ejecutor;
        _reloj = reloj;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ValidarConfiguracion(_configuracion);

        TimeSpan intervalo = TimeSpan.FromMinutes(_configuracion.IntervaloMinutos);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Sondeo iniciado. Cada {Minutos} minuto(s) sobre {Cantidad} repositorio(s).",
                _configuracion.IntervaloMinutos,
                _configuracion.Repositorios.Length);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await EjecutarUnaVueltaAsync(stoppingToken);
            await _reloj.EsperarAsync(intervalo, stoppingToken);
        }
    }

    /// <summary>
    /// Ejecuta exactamente una vuelta de sondeo. Pensado para tests que
    /// sustituyen <see cref="IReloj"/> y <see cref="IEjecutorVuelta"/> por dobles.
    /// </summary>
    /// <remarks>
    /// La vuelta se ejecuta en serie con cualquier otra llamada concurrente a este
    /// método: si el sondeo se despierta y la vuelta anterior sigue corriendo, la
    /// nueva espera a que termine antes de empezar. Asi nunca hay dos vueltas en
    /// paralelo pisandose.
    /// </remarks>
    public async Task EjecutarUnaVueltaAsync(CancellationToken cancelacion)
    {
        await _candadoVuelta.WaitAsync(cancelacion);
        try
        {
            await _ejecutor.EjecutarAsync(cancelacion);
        }
        finally
        {
            _candadoVuelta.Release();
        }
    }

    public static void ValidarConfiguracion(ConfiguracionSondeo configuracion)
    {
        if (configuracion is null)
        {
            throw new InvalidOperationException(
                "Falta la sección 'Sondeo' en la configuración. Añade 'Sondeo: { IntervaloMinutos, Repositorios }' al appsettings.json.");
        }

        if (configuracion.IntervaloMinutos <= 0)
        {
            throw new InvalidOperationException(
                $"Sondeo.IntervaloMinutos debe ser mayor que 0 (valor recibido: {configuracion.IntervaloMinutos}). Corrige appsettings.json → Sondeo.IntervaloMinutos.");
        }

        if (configuracion.Repositorios is null || configuracion.Repositorios.Length == 0)
        {
            throw new InvalidOperationException(
                "Sondeo.Repositorios está vacío. Añade al menos un repositorio en formato 'espacio/repo' en appsettings.json → Sondeo.Repositorios.");
        }

        for (int i = 0; i < configuracion.Repositorios.Length; i++)
        {
            string? repo = configuracion.Repositorios[i];
            if (string.IsNullOrWhiteSpace(repo) || !repo.Contains('/'))
            {
                throw new InvalidOperationException(
                    $"Sondeo.Repositorios[{i}] = '{repo}' no tiene el formato 'espacio/repo'. Corrige appsettings.json → Sondeo.Repositorios.");
            }
        }
    }
}
