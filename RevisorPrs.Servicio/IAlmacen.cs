namespace RevisorPrs.Servicio;

public interface IAlmacen
{
    void MarcarRevisado(string slugRepo, int idPr, string hashCommit);
    bool Revisado(string slugRepo, int idPr, string hashCommit);

    /// <summary>
    /// Devuelve todas las revisiones guardadas, para que el decisor pueda
    /// reconstruir su estado tras un reinicio del servicio.
    /// </summary>
    IEnumerable<(string Repositorio, int Numero, string Commit)> ListarRevisiones();

    /// <summary>
    /// Registra que un pull request ha fallado al procesarse (con su motivo),
    /// aplicando un backoff exponencial para que no se reintente en cada
    /// vuelta si el fallo es persistente (RV.18).
    /// </summary>
    void MarcarFallido(string slugRepo, int idPr, string hashCommit, string motivo);

    /// <summary>
    /// Devuelve true si el pull request debe reintentarse en esta vuelta,
    /// o false si aún estamos dentro del periodo de backoff por fallos previos.
    /// </summary>
    bool DebeReintentar(string slugRepo, int idPr, DateTimeOffset ahora);

    /// <summary>
    /// Devuelve todos los fallos registrados (repo, pr, último commit intentado, motivo).
    /// Pensado para que los tests verifiquen que el motivo del fallo se conserva.
    /// </summary>
    IEnumerable<(string Repositorio, int PullRequest, string Commit, string Motivo)> ListarFallos();
}
