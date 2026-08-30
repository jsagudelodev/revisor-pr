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

    public async Task<IReadOnlyList<Hallazgo>> RevisarAsync(string diff, CancellationToken token = default)
    {
        if (diff is null)
        {
            throw new ArgumentNullException(nameof(diff));
        }

        var respuesta = await EnviarAlLlmAsync(diff, token);
        return TraducirRespuesta(respuesta);
    }

    private async Task<string> EnviarAlLlmAsync(string diff, CancellationToken token)
    {
        var cuerpo = new
        {
            model = _config.Modelo,
            messages = new object[]
            {
                new { role = "system", content = PromptRevision.Mensaje },
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

    private static IReadOnlyList<Hallazgo> TraducirRespuesta(string contenidoJson)
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