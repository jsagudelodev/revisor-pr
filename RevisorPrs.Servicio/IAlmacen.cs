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

    /// <summary>
    /// Indica si el hallazgo con esta huella ya se comentó en el pull request.
    /// </summary>
    /// <remarks>
    /// Los comentarios se publican de uno en uno y el pull request no se da por
    /// revisado hasta el final. Sin este registro, una caída a mitad del bucle hacía
    /// que la vuelta siguiente republicara los comentarios que ya estaban en el PR.
    /// La huella la calcula <see cref="Hallazgo.Huella"/> y no depende del texto que
    /// genere el modelo, para que una reformulación no la despiste.
    /// </remarks>
    bool ComentarioPublicado(string slugRepo, int idPr, string huella);

    /// <summary>
    /// Deja constancia de que un comentario ya está publicado en el pull request.
    /// Repetir la llamada con la misma huella no tiene efecto.
    /// </summary>
    void MarcarComentarioPublicado(string slugRepo, int idPr, string hashCommit, string huella, string comentario);
}
