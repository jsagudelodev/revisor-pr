namespace RevisorPrs.Servicio;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly ConfiguracionSondeo _configuracion;
    private readonly IEjecutorVuelta _ejecutor;
    private readonly IReloj _reloj;
    private readonly EstadoServicio? _estado;
    private readonly ColaDeRevisiones? _cola;
    private readonly SemaphoreSlim _candadoVuelta = new SemaphoreSlim(1);

    /// <param name="estado">
    /// Estado observable que publica el endpoint /estado. Es el sondeo quien sabe
    /// cuándo toca la próxima vuelta, así que es aquí donde se anuncia. Opcional
    /// para los tests que solo ejercitan el ritmo del bucle.
    /// </param>
    public Worker(
        ILogger<Worker> logger,
        ConfiguracionSondeo configuracion,
        IEjecutorVuelta ejecutor,
        IReloj reloj,
        EstadoServicio? estado = null,
        ColaDeRevisiones? cola = null)
    {
        _logger = logger;
        _configuracion = configuracion;
        _ejecutor = ejecutor;
        _reloj = reloj;
        _estado = estado;
        _cola = cola;
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

            // Se anuncia DESPUÉS de la vuelta y antes de dormir: así el instante
            // publicado se cuenta desde que el sondeo se queda quieto de verdad,
            // no desde que empezó a trabajar.
            _estado?.AnunciarProximoSondeo(intervalo);

            await _reloj.EsperarAsync(intervalo, stoppingToken);

            // Antes de la vuelta siguiente se atiende lo que haya avisado el webhook.
            await AtenderAvisosAsync(stoppingToken);
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

    /// <summary>
    /// Revisa los pull requests que el webhook haya encolado.
    /// </summary>
    /// <remarks>
    /// Se atienden desde el MISMO hilo que el sondeo y bajo el mismo candado, a
    /// proposito: el almacen es una unica conexion SQLite y el decisor guarda estado en
    /// memoria. Revisar en paralelo desde el hilo que atiende el webhook seria pedir una
    /// carrera de datos.
    ///
    /// Lo que gana el equipo es la latencia: la revision arranca al atender el aviso, en
    /// vez de esperar a que venza el intervalo de sondeo.
    /// </remarks>
    public async Task AtenderAvisosAsync(CancellationToken cancelacion)
    {
        if (_cola is null)
        {
            return;
        }

        while (!cancelacion.IsCancellationRequested
            && _cola.IntentarSacar(out PullRequest? pr)
            && pr is not null)
        {
            await _candadoVuelta.WaitAsync(cancelacion);
            try
            {
                await _ejecutor.RevisarPrAsync(pr, cancelacion);
            }
            catch (OperationCanceledException) when (cancelacion.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Un aviso que revienta no puede tumbar el sondeo.
                _logger.LogError(
                    ex,
                    "Error revisando {Repositorio}#{Numero} desde un aviso de webhook.",
                    pr.Repositorio,
                    pr.Numero);
            }
            finally
            {
                _candadoVuelta.Release();
            }
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
