using RevisorPrs.Servicio;

namespace RevisorPrs.Tests;

internal sealed class RelojFalso : IReloj
{
    public int Llamadas { get; private set; }

    public async Task EsperarAsync(TimeSpan intervalo, CancellationToken cancelacion)
    {
        Llamadas++;
        // No esperamos de verdad, pero cedemos el control al planificador para que el
        // bucle del test no sea una espera activa que consuma el 100% de CPU.
        // Esto permite que el planificador de tareas procese la cancelación del test.
        await Task.Yield();
        cancelacion.ThrowIfCancellationRequested();
    }
}
