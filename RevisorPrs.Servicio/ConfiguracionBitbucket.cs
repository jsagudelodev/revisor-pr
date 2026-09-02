using System;

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
}