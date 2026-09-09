using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
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

    /// <summary>Pull requests revisados por aviso de webhook (C2).</summary>
    public List<PullRequest> RevisadosPorAviso { get; } = new();

    public Task RevisarPrAsync(PullRequest pr, CancellationToken cancelacion)
    {
        RevisadosPorAviso.Add(pr);
        return Task.CompletedTask;
    }
}
