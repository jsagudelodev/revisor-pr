using System.Collections.Generic;
using System.Threading;
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
    /// <param name="cancelacion">Token que aborta la llamada al pararse el servicio.</param>
    /// <returns>Secuencia de eventos de pull request.</returns>
    Task<IEnumerable<EventoPr>> ListarPrsAbiertos(string repositorio, CancellationToken cancelacion = default);

    /// <summary>
    /// Obtiene el diff de un pull request.
    /// </summary>
    /// <param name="repositorio">Nombre del repositorio en formato workspace/repo.</param>
    /// <param name="numero">Número del pull request.</param>
    /// <param name="cancelacion">Token que aborta la llamada al pararse el servicio.</param>
    /// <returns>
    /// Texto del diff. Una cadena vacía significa que el pull request NO tiene cambios,
    /// nunca que la llamada haya fallado: un fallo de la API se propaga como excepción.
    /// Quien lo consuma puede fiarse de que un diff vacío es un diff vacío de verdad.
    /// </returns>
    /// <exception cref="System.Net.Http.HttpRequestException">
    /// La API respondió con un código no exitoso o se agotaron los reintentos.
    /// </exception>
    Task<string> ObtenerDiff(string repositorio, int numero, CancellationToken cancelacion = default);

    /// <summary>
    /// Publica un comentario en un pull request.
    /// </summary>
    /// <param name="repositorio">Nombre del repositorio en formato workspace/repo.</param>
    /// <param name="numero">Número del pull request.</param>
    /// <param name="hallazgo">Hallazgo a publicar.</param>
    /// <param name="cancelacion">Token que aborta la llamada al pararse el servicio.</param>
    Task PublicarComentario(string repositorio, int numero, Hallazgo hallazgo, CancellationToken cancelacion = default);

    /// <summary>
    /// Publica un comentario general en el pull request, sin anclar a ninguna linea.
    /// </summary>
    /// <remarks>
    /// Lo usa el comentario de resumen, que no describe un hallazgo concreto sino la
    /// revision entera, asi que no puede pasar por <see cref="PublicarComentario"/>.
    /// </remarks>
    /// <param name="repositorio">Nombre del repositorio en formato workspace/repo.</param>
    /// <param name="numero">Numero del pull request.</param>
    /// <param name="texto">Cuerpo del comentario, en Markdown.</param>
    /// <param name="cancelacion">Token que aborta la llamada al pararse el servicio.</param>
    /// <exception cref="System.Net.Http.HttpRequestException">
    /// La API respondio con un codigo no exitoso o se agotaron los reintentos.
    /// </exception>
    Task PublicarComentarioGeneral(string repositorio, int numero, string texto, CancellationToken cancelacion = default);

    /// <summary>
    /// Descarga un archivo del repositorio en la referencia indicada (rama o commit).
    /// Devuelve null si no existe.
    /// </summary>
    /// <remarks>
    /// Lo usa la guia de convenciones del equipo (B3). Trae implementacion por defecto
    /// porque es capacidad opcional: un cliente que no sepa traer ficheros responde
    /// "no hay guia", que es exactamente lo que debe pasar.
    /// </remarks>
    Task<string?> ObtenerArchivo(
        string repositorio,
        string referencia,
        string ruta,
        CancellationToken cancelacion = default)
        => Task.FromResult<string?>(null);
}