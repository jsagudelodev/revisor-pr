using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RevisorPrs.Servicio;

/// <summary>
/// Revisa el diff de un pull request y devuelve los hallazgos encontrados.
/// </summary>
public interface IRevisor
{
    /// <summary>
    /// Envía el diff al LLM configurado y devuelve los hallazgos extraídos de su respuesta.
    /// </summary>
    /// <param name="diff">Diff completo del pull request a revisar.</param>
    /// <param name="token">Token de cancelación para abortar la llamada.</param>
    /// <returns>Lista de hallazgos encontrados; vacía si la respuesta no contiene ninguno.</returns>
    Task<IReadOnlyList<Hallazgo>> RevisarAsync(string diff, CancellationToken token = default);
}