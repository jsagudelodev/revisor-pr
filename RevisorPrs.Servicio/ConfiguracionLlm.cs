namespace RevisorPrs.Servicio;

/// <summary>
/// Configuración del proveedor de LLM que revisa los diffs.
/// </summary>
public class ConfiguracionLlm
{
    /// <summary>
    /// URL completa del endpoint compatible con la API de chat completions de OpenAI
    /// (por ejemplo, https://api.openai.com/v1/chat/completions).
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Nombre del modelo a utilizar (por ejemplo, "gpt-4o-mini").
    /// </summary>
    public string Modelo { get; set; } = string.Empty;

    /// <summary>
    /// Clave de API para autenticar las peticiones al proveedor.
    /// NUNCA se registra en logs.
    /// </summary>
    public string ClaveApi { get; set; } = string.Empty;
}