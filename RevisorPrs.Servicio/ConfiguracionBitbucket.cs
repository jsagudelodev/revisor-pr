using System;
using System.Collections.Generic;

namespace RevisorPrs.Servicio;

/// <summary>
/// Métodos de autenticación admitidos contra la API de Bitbucket Cloud.
/// </summary>
public enum MetodoAutenticacionBitbucket
{
    /// <summary>
    /// HTTP Basic con usuario y contraseña de aplicación (Basic auth).
    /// </summary>
    Basica,

    /// <summary>
    /// Bearer token de Bitbucket (token de workspace).
    /// </summary>
    Token,
}

/// <summary>
/// Configuración de autenticación para Bitbucket Cloud.
/// Debe rellenarse EXCLUSIVAMENTE con uno de los dos métodos:
/// - Basica: Usuario + ClaveAplicacion.
/// - Token: Token.
/// Cualquier otra combinación falla al arrancar con un mensaje accionable.
/// </summary>
public class ConfiguracionBitbucket
{
    /// <summary>
    /// Nombre de usuario de Bitbucket. Obligatorio solo si el método es Basica.
    /// </summary>
    public string Usuario { get; set; } = string.Empty;

    /// <summary>
    /// Contraseña de aplicación de Bitbucket. Obligatoria solo si el método es Basica.
    /// </summary>
    public string ClaveAplicacion { get; set; } = string.Empty;

    /// <summary>
    /// Token de workspace de Bitbucket. Obligatorio solo si el método es Token.
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Método de autenticación elegido por configuración. Por defecto, Basica
    /// (compatibilidad con configuraciones anteriores que solo rellenan usuario y clave).
    /// </summary>
    public MetodoAutenticacionBitbucket MetodoAutenticacion { get; set; } = MetodoAutenticacionBitbucket.Basica;

    /// <summary>
    /// Número máximo de intentos (incluyendo el primero) para llamadas a la API de Bitbucket.
    /// </summary>
    public int IntentosMaximos { get; set; } = 3;

    /// <summary>
    /// Tope en bytes para el diff enviado al modelo. Si el diff completo lo supera,
    /// se recortará por archivo y se listarán los omitidos al final.
    /// </summary>
    public int TopeBytesDiff { get; set; } = 100_000;

    /// <summary>
    /// Ruta, dentro del repositorio revisado, del fichero con las convenciones del
    /// equipo. Su contenido se anade al prompt para que el revisor aplique los criterios
    /// de ese equipo y no solo los genericos.
    ///
    /// Se lee de la RAMA DE DESTINO del pull request, nunca de la rama del propio PR:
    /// asi un pull request no puede relajar la revision que se le va a aplicar.
    /// Vacia desactiva la guia.
    /// </summary>
    public string RutaGuiaRepositorio { get; set; } = ".revisorpr.md";

    /// <summary>
    /// Lineas de contexto que se piden a Bitbucket alrededor de cada cambio.
    ///
    /// Sin contexto suficiente el modelo senala como errores cosas que estan definidas
    /// unas lineas por encima del recorte, y esa es la mayor fuente de falsos positivos.
    /// Se pide a la propia API del diff (parametro "context"), asi que no cuesta ninguna
    /// llamada extra ni hay que descargar los ficheros enteros.
    ///
    /// Sube el tamano del diff, asi que interactua con <see cref="TopeBytesDiff"/>: mas
    /// contexto significa que caben menos archivos antes del recorte. El valor por
    /// defecto (10) es un termino medio; git usa 3, que se queda corto para entender
    /// una funcion.
    /// </summary>
    public int LineasDeContexto { get; set; } = 10;

    /// <summary>
    /// Patrones glob de archivos que no se mandan a revisar: ficheros de bloqueo de
    /// dependencias, código generado, minificados y dependencias vendorizadas.
    /// Se aplican ANTES del tope de bytes, para que un fichero de bloqueo enorme no
    /// se coma el presupuesto del código que sí importa.
    ///
    /// Si no se configura nada se usa <see cref="ExclusionRutas.PorDefecto"/>. Una
    /// lista vacía en appsettings.json desactiva la exclusión y manda el diff entero.
    /// </summary>
    public string[]? RutasExcluidas { get; set; }

    /// <summary>
    /// Patrones efectivos: los configurados si se declararon, o los de serie.
    /// </summary>
    public IReadOnlyList<string> ResolverRutasExcluidas()
        => RutasExcluidas ?? ExclusionRutas.PorDefecto;
}