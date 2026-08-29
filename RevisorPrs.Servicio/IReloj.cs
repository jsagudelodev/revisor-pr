namespace RevisorPrs.Servicio;

public interface IReloj
{
    Task EsperarAsync(TimeSpan intervalo, CancellationToken cancelacion);
}
