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
}