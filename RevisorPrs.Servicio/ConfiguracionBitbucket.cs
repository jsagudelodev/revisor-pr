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
}