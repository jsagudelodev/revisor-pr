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

    /// <summary>
    /// Severidad mínima para conservar un hallazgo devuelto por el LLM.
    /// Severidades reconocidas (de menor a mayor): "baja", "media", "alta".
    /// Los hallazgos con severidad estrictamente menor se descartan como ruido.
    /// Vacío = sin filtrado por severidad (se conservan todos).
    /// </summary>
    public string SeveridadMinima { get; set; } = string.Empty;

    /// <summary>
    /// Tope de tokens para la RESPUESTA del modelo (parámetro max_tokens del proveedor).
    /// Un valor demasiado bajo hace que la respuesta llegue truncada a mitad de un JSON
    /// y se pierdan hallazgos (RV.10b). Por defecto se deja un valor generoso para que
    /// una revisión larga quepa entera. Se puede ajustar en appsettings.json.
    /// </summary>
    public int MaxTokensRespuesta { get; set; } = 8000;
}