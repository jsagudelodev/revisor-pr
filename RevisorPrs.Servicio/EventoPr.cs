namespace RevisorPrs.Servicio;

/// <summary>
/// Pull request abierto tal y como lo devuelve Bitbucket.
/// </summary>
/// <param name="Descripcion">
/// Texto que el autor escribio al abrir el pull request. Es opcional: muchos equipos
/// dejan la descripcion vacia, asi que su ausencia no invalida el evento.
/// </param>
public record EventoPr(
    string Repositorio,
    int Numero,
    string Commit,
    string Titulo,
    string Rama,
    string? Descripcion = null);
