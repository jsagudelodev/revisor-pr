namespace RevisorPrs.Servicio;

public interface IAlmacen
{
    void MarcarRevisado(string slugRepo, int idPr, string hashCommit);
    bool Revisado(string slugRepo, int idPr, string hashCommit);
}
