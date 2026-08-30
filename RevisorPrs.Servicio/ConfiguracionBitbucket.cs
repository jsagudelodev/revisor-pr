using System;

namespace RevisorPrs.Servicio;

/// <summary>
/// Configuración de autenticación para Bitbucket Cloud.
/// </summary>
public class ConfiguracionBitbucket
{
    /// <summary>
    /// Nombre de usuario de Bitbucket.
    /// </summary>
    public string Usuario { get; set; } = string.Empty;

    /// <summary>
    /// Contraseña de aplicación de Bitbucket.
    /// </summary>
    public string ClaveAplicacion { get; set; } = string.Empty;

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