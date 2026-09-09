namespace RevisorPrs.Servicio;

public interface IEjecutorVuelta
{
    /// <summary>
    /// Ejecuta una vuelta completa de sondeo sobre todos los repositorios configurados.
    /// </summary>
    Task EjecutarAsync(CancellationToken cancelacion);

    /// <summary>
    /// Revisa un unico pull request, sin pasar por el sondeo.
    /// </summary>
    /// <remarks>
    /// Lo usa el disparo por webhook. Comparte las guardas de idempotencia con el
    /// sondeo, asi que un webhook y una vuelta que coincidan no revisan dos veces.
    /// </remarks>
    Task RevisarPrAsync(PullRequest pr, CancellationToken cancelacion);
}
