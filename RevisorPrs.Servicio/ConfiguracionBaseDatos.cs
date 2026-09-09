namespace RevisorPrs.Servicio;

/// <summary>
/// Configuración del almacén SQLite que guarda las revisiones publicadas y los
/// intentos fallidos.
///
/// Existe para que <see cref="Almacen"/> se pueda construir desde el contenedor de
/// dependencias: su constructor recibe una ruta (un <c>string</c>), que el contenedor
/// no sabe resolver por sí solo. La sección se declara en appsettings.json como
/// <c>"BaseDatos": { "RutaBaseDatos": "..." }</c>.
/// </summary>
public class ConfiguracionBaseDatos
{
    /// <summary>
    /// Ruta del fichero SQLite. Si está vacía, <see cref="Almacen"/> usa
    /// <c>revisorprs.db</c> junto al ejecutable. Una ruta relativa se resuelve
    /// también respecto al directorio del ejecutable, NUNCA respecto al directorio
    /// de trabajo: un servicio de Windows arranca con un directorio de trabajo que
    /// no controlamos.
    /// </summary>
    public string RutaBaseDatos { get; set; } = string.Empty;
}
