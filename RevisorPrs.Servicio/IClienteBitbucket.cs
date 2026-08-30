using System.Collections.Generic;
using System.Threading.Tasks;
using RevisorPrs.Servicio;

namespace RevisorPrs.Servicio;

/// <summary>
/// Cliente para interactuar con la API de Bitbucket Cloud.
/// </summary>
public interface IClienteBitbucket
{
    /// <summary>
    /// Lista todos los pull requests abiertos de un repositorio, paginando hasta agotar resultados.
    /// </summary>
    /// <param name="repositorio">Nombre del repositorio en formato workspace/repo.</param>
    /// <returns>Secuencia de eventos de pull request.</returns>
    Task<IEnumerable<EventoPr>> ListarPrsAbiertos(string repositorio);

    /// <summary>
    /// Obtiene el diff de un pull request.
    /// </summary>
    /// <param name="repositorio">Nombre del repositorio en formato workspace/repo.</param>
    /// <param name="numero">Número del pull request.</param>
    /// <returns>Texto del diff, o cadena vacía si la API falla.</returns>
    Task<string> ObtenerDiff(string repositorio, int numero);
}