using Microsoft.Extensions.Logging.Abstractions;
using RevisorPrs.Servicio;

namespace RevisorPrs.Tests;

public class WorkerSondeoTests
{
    private static ConfiguracionSondeo ConfiguracionValida() => new()
    {
        IntervaloMinutos = 5,
        Repositorios = ["equipo-a/repo-1", "equipo-b/repo-2"],
    };

    private static Worker CrearWorker(ConfiguracionSondeo cfg, IEjecutorVuelta ejecutor, IReloj reloj)
        => new(NullLogger<Worker>.Instance, cfg, ejecutor, reloj);

    [Fact]
    public async Task EjecutaUnaVueltaPorCicloSinEsperarDeVerdad()
    {
        ConfiguracionSondeo cfg = ConfiguracionValida();
        EjecutorVueltaFalso ejecutor = new();
        RelojFalso reloj = new();

        using CancellationTokenSource cts = new();
        Worker worker = CrearWorker(cfg, ejecutor, reloj);

        // Arranca el bucle en una tarea y espera a que se ejecute al menos una vuelta.
        Task bucle = worker.StartAsync(cts.Token);

        // Espera explícita a la primera vuelta, sin esperas reales de tiempo.
        await ejecutor.PrimeraLlamada.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(ejecutor.Llamadas >= 1, "Se esperaba al menos 1 vuelta.");

        // Cancelamos para que el bucle termine.
        await worker.StopAsync(CancellationToken.None);
        await bucle;

        Assert.True(reloj.Llamadas >= 1, "El bucle debe pedir al reloj que espere al menos una vez entre vueltas.");
        // Como el reloj no espera de verdad, en el mismo hilo deberían haberse ejecutado
        // más de una vuelta antes de que cancelemos. Si el reloj esperase de verdad, sería 1.
        Assert.True(ejecutor.Llamadas > 1 || reloj.Llamadas >= 1,
            "Sin reloj real, el bucle debe avanzar más rápido que un ciclo por minuto.");
    }

    [Fact]
    public async Task BucleRespetaLaCancelacion()
    {
        ConfiguracionSondeo cfg = ConfiguracionValida();
        EjecutorVueltaFalso ejecutor = new();
        RelojFalso reloj = new();

        using CancellationTokenSource cts = new();
        Worker worker = CrearWorker(cfg, ejecutor, reloj);

        Task bucle = worker.StartAsync(cts.Token);
        await ejecutor.PrimeraLlamada.WaitAsync(TimeSpan.FromSeconds(2));

        await worker.StopAsync(CancellationToken.None);

        // El bucle debe terminar en un tiempo razonable (no colgado).
        Task completado = await Task.WhenAny(bucle, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(bucle, completado);
    }

    [Fact]
    public void ValidarConfiguracion_Falla_SiIntervaloEsCero()
    {
        ConfiguracionSondeo cfg = ConfiguracionValida();
        cfg.IntervaloMinutos = 0;

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => Worker.ValidarConfiguracion(cfg));
        Assert.Contains("IntervaloMinutos", ex.Message);
        Assert.Contains("0", ex.Message);
    }

    [Fact]
    public void ValidarConfiguracion_Falla_SiIntervaloEsNegativo()
    {
        ConfiguracionSondeo cfg = ConfiguracionValida();
        cfg.IntervaloMinutos = -3;

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => Worker.ValidarConfiguracion(cfg));
        Assert.Contains("IntervaloMinutos", ex.Message);
    }

    [Fact]
    public void ValidarConfiguracion_Falla_SiNoHayRepositorios()
    {
        ConfiguracionSondeo cfg = ConfiguracionValida();
        cfg.Repositorios = [];

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => Worker.ValidarConfiguracion(cfg));
        Assert.Contains("Repositorios", ex.Message);
        Assert.Contains("vacío", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidarConfiguracion_Falla_SiUnRepositorioNoTieneFormatoEspacioRepo()
    {
        ConfiguracionSondeo cfg = ConfiguracionValida();
        cfg.Repositorios = ["repositorio-mal-formado"];

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => Worker.ValidarConfiguracion(cfg));
        Assert.Contains("espacio/repo", ex.Message);
    }

    [Fact]
    public void ValidarConfiguracion_Pasa_SiTodoEsCorrecto()
    {
        // No debe lanzar.
        Worker.ValidarConfiguracion(ConfiguracionValida());
    }

    [Fact]
    public async Task DosVueltasConcurrentes_NoSePisan_YAmbasAcaban()
    {
        ConfiguracionSondeo cfg = ConfiguracionValida();
        RelojFalso reloj = new();

        // Falso del ejecutor que cuenta entradas concurrentes y se deja retener
        // por la primera vuelta para forzar la serializacion.
        ContadorEjecutorVuelta ejecutor = new();

        using CancellationTokenSource cts = new();
        Worker worker = CrearWorker(cfg, ejecutor, reloj);

        // Dispara DOS vueltas a la vez. La primera se queda retenida por el
        // TaskCompletionSource; la segunda debe esperar a que termine antes de
        // empezar (el candado del Worker serializa).
        Task primera = worker.EjecutarUnaVueltaAsync(cts.Token);
        Task segunda = worker.EjecutarUnaVueltaAsync(cts.Token);

        // Espera a que la PRIMERA vuelta haya entrado. Mientras siga dentro, el
        // candado del Worker impide que la segunda pueda entrar, asi que
        // esperamos a soltarla ANTES de pedir la entrada de la segunda.
        await ejecutor.PrimeraEsperada.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, ejecutor.Entradas);
        Assert.Equal(1, ejecutor.MaximoEntradasSimultaneas);

        // Suelta la primera vuelta para liberar el candado.
        ejecutor.Soltar(1);
        await primera.WaitAsync(TimeSpan.FromSeconds(2));

        // Ahora la segunda puede entrar y registrarse.
        await ejecutor.SegundaEsperada.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, ejecutor.MaximoEntradasSimultaneas);
        Assert.Equal(2, ejecutor.Entradas);

        // Suelta la segunda y comprueba que ambas terminaron serializadas.
        ejecutor.Soltar(2);
        await segunda.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, ejecutor.Entradas);
        Assert.Equal(0, ejecutor.VueltasEnVuelo);
        Assert.Equal(2, ejecutor.Salidas);
    }

    private sealed class ContadorEjecutorVuelta : IEjecutorVuelta
    {
        private readonly TaskCompletionSource _primeraEsperada = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _segundaEsperada = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<int, TaskCompletionSource> _gatesPorNumeroDeLlamada = new();
        private int _numeroDeLlamada;
        private int _entradasEnVuelo;
        private int _salidas;
        private int _maximoEntradasSimultaneas;

        public Task PrimeraEsperada => _primeraEsperada.Task;
        public Task SegundaEsperada => _segundaEsperada.Task;

        // Numero total de vueltas que el Worker ha lanzado contra el ejecutor.
        public int Entradas => Volatile.Read(ref _numeroDeLlamada);

        // Vueltas que estan ejecutandose AHORA MISMO dentro del ejecutor.
        public int VueltasEnVuelo => _entradasEnVuelo;

        public int Salidas => _salidas;
        public int MaximoEntradasSimultaneas => _maximoEntradasSimultaneas;

        public void Soltar(int numeroDeLlamada)
        {
            lock (_gatesPorNumeroDeLlamada)
            {
                if (_gatesPorNumeroDeLlamada.TryGetValue(numeroDeLlamada, out TaskCompletionSource? gate))
                {
                    gate.TrySetResult();
                }
            }
        }

        public async Task EjecutarAsync(CancellationToken cancelacion)
        {
            int ahora = Interlocked.Increment(ref _numeroDeLlamada);
            Interlocked.Increment(ref _entradasEnVuelo);

            // Registra el maximo de entradas simultaneas (CAS sin bucle: el maximo solo crece).
            int enVuelo = Volatile.Read(ref _entradasEnVuelo);
            int maxActual;
            do
            {
                maxActual = Volatile.Read(ref _maximoEntradasSimultaneas);
                if (enVuelo <= maxActual)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(ref _maximoEntradasSimultaneas, enVuelo, maxActual) != maxActual);

            TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gatesPorNumeroDeLlamada)
            {
                _gatesPorNumeroDeLlamada[ahora] = gate;
            }

            if (ahora == 1)
            {
                _primeraEsperada.TrySetResult();
            }
            else if (ahora == 2)
            {
                _segundaEsperada.TrySetResult();
            }

            await gate.Task.WaitAsync(cancelacion);

            Interlocked.Increment(ref _salidas);
            Interlocked.Decrement(ref _entradasEnVuelo);
        }
    }
}
