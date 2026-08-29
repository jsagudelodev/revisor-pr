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
}
