namespace RevisorPrs.Servicio;

public class RelojSistema : IReloj
{
    public async Task EsperarAsync(TimeSpan intervalo, CancellationToken cancelacion)
    {
        await Task.Delay(intervalo, cancelacion);
    }
}
