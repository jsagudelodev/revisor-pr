using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RevisorPrs.Servicio;

/// <summary>
/// Revisor que envía el diff a un LLM por HTTP y traduce la respuesta JSON
/// en una lista de <see cref="Hallazgo"/>.
/// El endpoint debe ser compatible con la API de chat completions de OpenAI
/// (POST con cabecera Authorization: Bearer y cuerpo { model, messages, response_format }).
/// </summary>
public class Revisor : IRevisor
{
    private readonly HttpClient _httpClient;
    private readonly ConfiguracionLlm _config;
    private readonly ILogger<Revisor> _logger;

    private const string PromptUsuario =
        "Analiza el siguiente diff de un pull request y devuelve los hallazgos en JSON:\n\n{0}";

    public Revisor(
        HttpClient httpClient,
        IOptions<ConfiguracionLlm> config,
        ILogger<Revisor> logger)
    {
        _httpClient = httpClient;
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>
    /// Mensaje explícito que se usa en el reintento para forzar al LLM a devolver
    /// ÚNICAMENTE un JSON válido (sin prosa alrededor).
    /// </summary>
    private const string MensajeReintento =
        "Tu respuesta anterior no fue un JSON válido. Responde ÚNICAMENTE con un JSON válido que cumpla el formato pedido, sin texto antes ni después.";

    public async Task<ResultadoRevision> RevisarAsync(string diff, CancellationToken token = default)
    {
        if (diff is null)
        {
            throw new ArgumentNullException(nameof(diff));
        }

        // Primer intento con el prompt habitual.
        var contenidoCrudo = await EnviarAlLlmAsync(diff, PromptRevision.Mensaje, token);
        var (ok, json, motivo) = IntentarExtraerJson(contenidoCrudo);
        if (ok)
        {
            return ResultadoRevision.Ok(ParsearHallazgos(json!));
        }

        // Un único reintento pidiendo explícitamente solo JSON.
        _logger.LogWarning(
            "La respuesta del LLM no es JSON válido ({Motivo}). Se reintenta una vez pidiendo solo JSON.",
            motivo);

        contenidoCrudo = await EnviarAlLlmAsync(diff, MensajeReintento, token);
        (ok, json, motivo) = IntentarExtraerJson(contenidoCrudo);
        if (ok)
        {
            return ResultadoRevision.Ok(ParsearHallazgos(json!));
        }

        // Segundo intento también inválido: marcamos el PR como FALLIDO sin hallazgos
        // para que NUNCA se publique basura.
        _logger.LogError(
            "La respuesta del LLM no es JSON válido tras un reintento ({Motivo}). Se marca el PR como fallido.",
            motivo);

        return ResultadoRevision.Fallo(
            $"La respuesta del LLM no es JSON válido tras un reintento ({motivo}).");
    }

    private async Task<string> EnviarAlLlmAsync(string diff, string mensajeSistema, CancellationToken token)
    {
        var cuerpo = new
        {
            model = _config.Modelo,
            messages = new object[]
            {
                new { role = "system", content = mensajeSistema },
                new { role = "user", content = string.Format(PromptUsuario, diff) },
            },
            response_format = new { type = "json_object" },
        };

        var json = JsonSerializer.Serialize(cuerpo);
        using var request = new HttpRequestMessage(HttpMethod.Post, _config.Endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ClaveApi);

        // Logueamos la longitud del diff para diagnóstico, nunca su contenido ni la clave.
        _logger.LogInformation(
            "Enviando diff al LLM para revisión ({Caracteres} caracteres).",
            diff.Length);

        using var response = await _httpClient.SendAsync(request, token);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;
    }

    /// <summary>
    /// Intenta extraer un JSON válido de la respuesta cruda del LLM.
    /// Si viene envuelta en un bloque markdown (```json ... ``` o ``` ... ```),
    /// se extrae el contenido del bloque sin reintentar: es la forma más común y NO es error.
    /// El bloque puede estar precedido o seguido de prosa; se busca en cualquier posición.
    /// Devuelve (true, json, null) si fue posible, o (false, null, motivo) si no.
    /// </summary>
    private static (bool Ok, string? Json, string? Motivo) IntentarExtraerJson(string contenidoCrudo)
    {
        if (string.IsNullOrWhiteSpace(contenidoCrudo))
        {
            return (false, null, "respuesta vacía");
        }

        var texto = contenidoCrudo.Trim();

        // Si la respuesta cruda NO es JSON, probamos a extraer un bloque markdown
        // (```json ... ``` o ``` ... ```) que pueda estar en cualquier posición del texto.
        if (!EsJsonValido(texto))
        {
            texto = ExtraerJsonDeBloqueMarkdown(texto);
        }

        if (EsJsonValido(texto))
        {
            return (true, texto, null);
        }

        return (false, null, "la respuesta no contiene un JSON válido");
    }

    private static bool EsJsonValido(string texto)
    {
        try
        {
            using var doc = JsonDocument.Parse(texto);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Busca un bloque markdown (```json ... ``` o ``` ... ```) en cualquier posición del texto
    /// y devuelve su contenido interior. Si no encuentra ninguno, devuelve el texto sin cambios.
    /// </summary>
    private static string ExtraerJsonDeBloqueMarkdown(string texto)
    {
        const string AperturaConLenguaje = "```json";
        const string AperturaGenerica = "```";

        var indiceApertura = texto.IndexOf(AperturaConLenguaje, StringComparison.OrdinalIgnoreCase);
        if (indiceApertura < 0)
        {
            indiceApertura = texto.IndexOf(AperturaGenerica, StringComparison.Ordinal);
        }
        if (indiceApertura < 0)
        {
            return texto;
        }

        var inicioContenido = texto.IndexOf('\n', indiceApertura);
        if (inicioContenido < 0)
        {
            return texto;
        }

        var finBloque = texto.IndexOf(AperturaGenerica, inicioContenido + 1, StringComparison.Ordinal);
        if (finBloque < 0)
        {
            return texto;
        }

        return texto.Substring(inicioContenido + 1, finBloque - inicioContenido - 1).Trim();
    }

    private static IReadOnlyList<Hallazgo> ParsearHallazgos(string contenidoJson)
    {
        if (string.IsNullOrWhiteSpace(contenidoJson))
        {
            return Array.Empty<Hallazgo>();
        }

        using var doc = JsonDocument.Parse(contenidoJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("hallazgos", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Hallazgo>();
        }

        var resultados = new List<Hallazgo>();
        foreach (var elemento in array.EnumerateArray())
        {
            var hallazgo = new Hallazgo(
                Archivo: ObtenerString(elemento, "Archivo") ?? string.Empty,
                Linea: ObtenerLinea(elemento, "Linea"),
                Severidad: ObtenerString(elemento, "Severidad") ?? "info",
                Resumen: ObtenerString(elemento, "Resumen") ?? string.Empty,
                Detalle: ObtenerString(elemento, "Detalle") ?? string.Empty);
            resultados.Add(hallazgo);
        }
        return resultados;
    }

    private static string? ObtenerString(JsonElement elemento, string propiedad)
    {
        if (!elemento.TryGetProperty(propiedad, out var valor) || valor.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return valor.ValueKind == JsonValueKind.String ? valor.GetString() : valor.ToString();
    }

    private static int? ObtenerLinea(JsonElement elemento, string propiedad)
    {
        if (!elemento.TryGetProperty(propiedad, out var valor) || valor.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (valor.ValueKind == JsonValueKind.Number && valor.TryGetInt32(out var n))
        {
            return n;
        }
        return null;
    }
}