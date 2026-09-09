namespace RevisorPrs.Servicio;

/// <summary>
/// Lo que el modelo necesita saber del pull request, ademas del diff.
/// </summary>
/// <remarks>
/// Existe como tipo propio y no como parametros sueltos porque va a crecer: la guia de
/// convenciones por repositorio (B3) entra aqui sin volver a tocar la firma de
/// <see cref="IRevisor.RevisarAsync"/> ni todos sus dobles de prueba.
///
/// Ojo: <see cref="Titulo"/> y <see cref="Descripcion"/> los escribe quien abre el pull
/// request, asi que son texto NO confiable. El prompt los delimita y avisa al modelo de
/// que son datos a analizar y no instrucciones.
/// </remarks>
/// <param name="Titulo">Titulo del pull request, si se conoce.</param>
/// <param name="Descripcion">Descripcion que escribio el autor, si la hay.</param>
/// <param name="Guia">
/// Convenciones del equipo, leidas del repositorio revisado. A diferencia del titulo y
/// la descripcion, este texto vive en la rama de destino y ha pasado por la revision del
/// equipo para llegar ahi, asi que es mucho mas de fiar que lo que escribe un PR suelto.
/// </param>
public record ContextoRevision(
    string? Titulo = null,
    string? Descripcion = null,
    string? Guia = null)
{
    /// <summary>Contexto vacio: solo el diff, sin intencion declarada.</summary>
    public static ContextoRevision Ninguno { get; } = new();

    /// <summary>Indica si aporta algo que merezca ocupar sitio en el prompt.</summary>
    public bool TieneAlgo =>
        !string.IsNullOrWhiteSpace(Titulo) || !string.IsNullOrWhiteSpace(Descripcion);

    /// <summary>Indica si hay convenciones del equipo que anadir al prompt.</summary>
    public bool TieneGuia => !string.IsNullOrWhiteSpace(Guia);
}
