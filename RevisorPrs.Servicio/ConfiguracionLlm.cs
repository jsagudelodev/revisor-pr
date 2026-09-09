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
    /// Severidad a partir de la cual un hallazgo se comenta ANCLADO a su línea del diff.
    /// Los que quedan por debajo se agrupan en un único comentario de resumen.
    ///
    /// Por defecto "error": solo lo grave interrumpe con una notificación propia, y el
    /// resto llega junto. Es lo que evita que un pull request con doce hallazgos genere
    /// doce avisos a cada persona suscrita.
    ///
    /// Admite los dos vocabularios ("error"/"warning"/"info" y "alta"/"media"/"baja").
    /// Con "baja" se ancla todo, que es el comportamiento anterior.
    /// </summary>
    public string SeveridadAnclada { get; set; } = "error";

    /// <summary>
    /// Tarifa por millon de tokens de ENTRADA, para estimar el gasto en /estado.
    /// Cero (por defecto) significa que no se estima nada y solo se informan tokens.
    /// </summary>
    /// <remarks>
    /// La tarifa la pone el equipo porque los precios cambian y varian por modelo:
    /// codificarlos en el servicio seria garantizar que envejecen mal. La moneda es la
    /// que use tu proveedor; el servicio solo multiplica.
    /// </remarks>
    public decimal CostePorMillonEntrada { get; set; }

    /// <summary>
    /// Tarifa por millon de tokens de SALIDA. Suele ser bastante mas cara que la de
    /// entrada, de ahi que se configuren por separado.
    /// </summary>
    public decimal CostePorMillonSalida { get; set; }

    /// <summary>
    /// Tope de tokens para la RESPUESTA del modelo (parámetro max_tokens del proveedor).
    /// Un valor demasiado bajo hace que la respuesta llegue truncada a mitad de un JSON
    /// y se pierdan hallazgos (RV.10b). Por defecto se deja un valor generoso para que
    /// una revisión larga quepa entera. Se puede ajustar en appsettings.json.
    /// </summary>
    public int MaxTokensRespuesta { get; set; } = 8000;
}