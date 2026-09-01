using System;
using System.IO;

namespace RevisorPrs.Servicio;

/// <summary>
/// Configuración del log a fichero (RV.21).
///
/// El servicio escribe su log a un fichero, ademas de la consola, y rota
/// por tamaño para no crecer sin limite. La ruta por defecto vive junto al
/// ejecutable del servicio, NUNCA en %TEMP%: en una maquina de cliente el
/// directorio temporal se borra entre reinicios y el soporte no tendria
/// forma de leer el log.
/// </summary>
public class ConfiguracionRegistro
{
    /// <summary>
    /// Nombre por defecto del fichero de log. Se resuelve junto al ejecutable.
    /// </summary>
    public const string NombreFicheroPorDefecto = "revisor-prs.log";

    /// <summary>
    /// Tamaño maximo del fichero antes de rotar. Por defecto 5 MiB.
    /// </summary>
    public const long TamanoMaximoPorDefectoBytes = 5L * 1024 * 1024;

    /// <summary>
    /// Numero maximo de ficheros a conservar (incluyendo el activo). Por defecto 5.
    /// </summary>
    public const int FicherosConservadosPorDefecto = 5;

    /// <summary>
    /// Ruta del fichero de log. Si esta vacia, se resuelve junto al ejecutable.
    /// </summary>
    public string RutaFichero { get; set; } = string.Empty;

    /// <summary>
    /// Tamano maximo en bytes antes de rotar. Un valor <= 0 desactiva la rotacion
    /// por tamano (se seguira escribiendo en el mismo fichero).
    /// </summary>
    public long TamanoMaximoBytes { get; set; } = TamanoMaximoPorDefectoBytes;

    /// <summary>
    /// Numero de ficheros a conservar contando el activo.
    /// Un valor <= 1 mantiene solo el fichero activo (los antiguos se eliminan).
    /// </summary>
    public int FicherosConservados { get; set; } = FicherosConservadosPorDefecto;

    /// <summary>
    /// Devuelve la ruta final del fichero de log: la configurada si no esta
    /// vacia, o una ruta junto al ejecutable si esta vacia. Nunca en %TEMP%.
    /// </summary>
    public string ResolverRuta()
    {
        if (!string.IsNullOrWhiteSpace(RutaFichero))
        {
            return RutaFichero;
        }

        string? directorio = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrWhiteSpace(directorio))
        {
            directorio = AppContext.BaseDirectory;
        }
        return Path.Combine(directorio!, NombreFicheroPorDefecto);
    }

    /// <summary>
    /// Valida que los valores sean accionables. Lanza <see cref="InvalidOperationException"/>
    /// con mensaje claro si algo esta mal.
    /// </summary>
    public static void ValidarConfiguracion(ConfiguracionRegistro? configuracion)
    {
        if (configuracion is null)
        {
            throw new InvalidOperationException(
                "Falta la seccion 'Registro' en la configuracion. Anade 'Registro: { RutaFichero, TamanoMaximoBytes, FicherosConservados }' al appsettings.json.");
        }

        if (configuracion.TamanoMaximoBytes < 0)
        {
            throw new InvalidOperationException(
                $"Registro.TamanoMaximoBytes debe ser mayor o igual que 0 (valor recibido: {configuracion.TamanoMaximoBytes}). 0 desactiva la rotacion por tamano.");
        }

        if (configuracion.FicherosConservados < 1)
        {
            throw new InvalidOperationException(
                $"Registro.FicherosConservados debe ser al menos 1 (valor recibido: {configuracion.FicherosConservados}).");
        }
    }
}