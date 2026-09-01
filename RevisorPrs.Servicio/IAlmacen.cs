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
}
