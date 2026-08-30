using System.Threading;
using System.Threading.Tasks;

namespace RevisorPrs.Servicio;

/// <summary>
/// Revisa el diff de un pull request y devuelve un <see cref="ResultadoRevision"/>
/// que indica tanto los hallazgos encontrados como si la revisión tuvo éxito.
/// </summary>
public interface IRevisor
{
    /// <summary>
    /// Envía el diff al LLM configurado. Si la respuesta no es JSON válido, reintenta
    /// una sola vez pidiendo explícitamente solo JSON. Si tras el reintento la respuesta
    /// sigue sin ser válida, devuelve un <see cref="ResultadoRevision"/> con
    /// <c>Exito = false</c> y sin hallazgos: NUNCA se publica basura en un PR.
    /// </summary>
    /// <param name="diff">Diff completo del pull request a revisar.</param>
    /// <param name="token">Token de cancelación para abortar la llamada.</param>
    /// <returns>
    /// Un <see cref="ResultadoRevision"/> con los hallazgos, o con el motivo del fallo.
    /// </returns>
    Task<ResultadoRevision> RevisarAsync(string diff, CancellationToken token = default);
}