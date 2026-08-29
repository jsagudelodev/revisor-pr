using RevisorPrs.Servicio;

namespace RevisorPrs.Tests;

internal sealed class EjecutorVueltaFalso : IEjecutorVuelta
{
    private readonly TaskCompletionSource _primeraLlamadaTcs = new();

    public int Llamadas { get; private set; }
    public Task PrimeraLlamada => _primeraLlamadaTcs.Task;

    public Task EjecutarAsync(CancellationToken cancelacion)
    {
        Llamadas++;
        _primeraLlamadaTcs.TrySetResult();
        return Task.CompletedTask;
    }
}
