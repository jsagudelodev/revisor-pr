namespace RevisorPrs.Servicio;

public interface IEjecutorVuelta
{
    Task EjecutarAsync(CancellationToken cancelacion);
}
