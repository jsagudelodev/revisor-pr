using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RevisorPrs.Servicio;

namespace RevisorPrs.Servicio;

/// <summary>
/// Implementación de IClienteBitbucket que llama a la API de Bitbucket Cloud.
/// </summary>
public class ClienteBitbucket : IClienteBitbucket
{
    private readonly HttpClient _httpClient;
    private readonly ConfiguracionBitbucket _config;
    private readonly ILogger<ClienteBitbucket> _logger;
    private readonly TraductorEventoPr _traductor;

    /// <summary>
    /// Función de espera entre reintentos. Inyectable para que los tests no tarden segundos.
    /// </summary>
    public Func<int, CancellationToken, Task> EsperarEntreReintentos { get; set; }

    public ClienteBitbucket(
        HttpClient httpClient,
        IOptions<ConfiguracionBitbucket> config,
        ILogger<ClienteBitbucket> logger,
        TraductorEventoPr traductor)
    {
        _httpClient = httpClient;
        _config = config.Value;
        _logger = logger;
        _traductor = traductor;
        EsperarEntreReintentos = EsperarEntreReintentosPorDefecto;
    }

    public async Task<IEnumerable<EventoPr>> ListarPrsAbiertos(string repositorio)
    {
        var result = new List<EventoPr>();
        string? url = $"https://api.bitbucket.org/2.0/repositories/{repositorio}/pullrequests?state=OPEN";

        while (!string.IsNullOrEmpty(url))
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url!);
            PonerAutenticacionBasica(request);

            var response = await EnviarConReintentos(request, repositorio, "listar PRs");

            if (response is null)
            {
                break;
            }

            // 4xx (no 429) y 5xx al agotar el reintento devuelven la respuesta sin lanzar;
            // aquí preservamos el contrato original: si no es éxito, lanzamos.
            if (!response.IsSuccessStatusCode)
            {
                var codigo = (int)response.StatusCode;
                _logger.LogError(
                    "Error accionable: respuesta no exitosa al listar PRs en {Repo}. Código HTTP: {Codigo}",
                    repositorio, codigo);
                response.Dispose();
                throw new HttpRequestException(
                    $"Respuesta no exitosa al listar PRs en {repositorio}: {codigo}",
                    inner: null,
                    statusCode: response.StatusCode);
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var jsonDoc = await JsonDocument.ParseAsync(stream);
            var root = jsonDoc.RootElement;

            if (root.TryGetProperty("values", out var values))
            {
                foreach (var element in values.EnumerateArray())
                {
                    var evento = _traductor.Traducir(element);
                    if (evento != null)
                    {
                        result.Add(evento);
                    }
                }
            }

            if (root.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String)
            {
                url = next.GetString();
            }
            else
            {
                url = null;
            }
        }

        return result;
    }

    public async Task<string> ObtenerDiff(string repositorio, int numero)
    {
        if (string.IsNullOrEmpty(repositorio))
        {
            _logger.LogWarning("Repositorio no especificado para obtener diff.");
            return string.Empty;
        }

        string url = $"https://api.bitbucket.org/2.0/repositories/{repositorio}/pullrequests/{numero}/diff";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        PonerAutenticacionBasica(request);

        var response = await EnviarConReintentos(request, repositorio, $"obtener diff PR #{numero}");

        if (response is null)
        {
            return string.Empty;
        }

        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadAsStringAsync();
        }

        _logger.LogWarning("Error al obtener diff de Bitbucket: {StatusCode} para {Repo} PR #{Numero}",
            (int)response.StatusCode, repositorio, numero);
        return string.Empty;
    }

    public async Task PublicarComentario(string repositorio, int numero, Hallazgo hallazgo)
    {
        if (string.IsNullOrEmpty(repositorio))
        {
            _logger.LogWarning("Repositorio no especificado para publicar comentario.");
            return;
        }

        string url = $"https://api.bitbucket.org/2.0/repositories/{repositorio}/pullrequests/{numero}/comments";

        object payload;
        if (!string.IsNullOrEmpty(hallazgo.Archivo) && hallazgo.Linea.HasValue)
        {
            payload = new
            {
                content = new { raw = hallazgo.Resumen },
                inline = new { path = hallazgo.Archivo, to = hallazgo.Linea.Value }
            };
        }
        else
        {
            var lineNumber = hallazgo.Linea ?? 0;
            payload = new
            {
                content = new { raw = $"{hallazgo.Archivo}:{lineNumber} {hallazgo.Resumen}" }
            };
        }

        var json = JsonSerializer.Serialize(payload);
        var requestMsg = new HttpRequestMessage(HttpMethod.Post, url);
        PonerAutenticacionBasica(requestMsg);
        requestMsg.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await EnviarConReintentos(requestMsg, repositorio, $"publicar comentario PR #{numero}");

        if (response is null)
        {
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Error al publicar comentario en Bitbucket: {StatusCode} para {Repo} PR #{Numero}",
                (int)response.StatusCode, repositorio, numero);
        }
    }

    /// <summary>
    /// Envía una petición HTTP reintentando ante respuestas 429 y 5xx con espera creciente.
    /// Un 4xx que no sea 429 NO se reintenta. Al agotar el tope, registra un error
    /// accionable con repositorio, PR y código HTTP y devuelve null (NO lanza).
    /// </summary>
    private async Task<HttpResponseMessage?> EnviarConReintentos(
        HttpRequestMessage request,
        string repositorio,
        string operacion)
    {
        var intentosMaximos = _config.IntentosMaximos > 0 ? _config.IntentosMaximos : 1;
        var cancellationToken = CancellationToken.None;

        HttpResponseMessage? response = null;
        int? ultimoCodigo = null;

        for (int intento = 1; intento <= intentosMaximos; intento++)
        {
            var intentoRequest = await ClonarPeticionAsync(request);

            try
            {
                response = await _httpClient.SendAsync(intentoRequest, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex,
                    "Error de red al {Operacion} en {Repo} (intento {Intento}/{Max})",
                    operacion, repositorio, intento, intentosMaximos);
                response = null;
            }

            ultimoCodigo = response is null ? null : (int?)response.StatusCode;

            bool exito = response is not null && response.IsSuccessStatusCode;
            bool seDebeReintentar = response is not null
                && ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500);

            if (exito)
            {
                return response;
            }

            if (response is not null && !seDebeReintentar)
            {
                return response;
            }

            if (intento < intentosMaximos)
            {
                response?.Dispose();
                response = null;
                var esperaMs = CalcularEsperaMs(intento);
                try
                {
                    await EsperarEntreReintentos(esperaMs, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
        }

        _logger.LogError(
            "Error accionable: se agotaron los reintentos al {Operacion} en {Repo} tras {Max} intentos. Último código HTTP: {Codigo}",
            operacion, repositorio, intentosMaximos, ultimoCodigo?.ToString() ?? "N/D");

        response?.Dispose();
        return null;
    }

    private static int CalcularEsperaMs(int intento)
    {
        return 200 * (int)Math.Pow(2, intento - 1);
    }

    private static Task EsperarEntreReintentosPorDefecto(int milisegundos, CancellationToken cancellationToken)
    {
        return Task.Delay(milisegundos, cancellationToken);
    }

    private static async Task<HttpRequestMessage> ClonarPeticionAsync(HttpRequestMessage original)
    {
        var clon = new HttpRequestMessage(original.Method, original.RequestUri);
        if (original.Content != null)
        {
            // Bufferizamos en bytes para poder releer el contenido en cada reintento
            // (HttpClient dispone el Stream del Content tras enviarlo la primera vez).
            var bytes = await original.Content.ReadAsByteArrayAsync();
            var mediaType = original.Content.Headers.ContentType?.MediaType ?? "application/json";
            clon.Content = new ByteArrayContent(bytes);
            clon.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        }
        foreach (var header in original.Headers)
        {
            clon.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return clon;
    }

    private void PonerAutenticacionBasica(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(_config.Usuario) && !string.IsNullOrEmpty(_config.ClaveAplicacion))
        {
            var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_config.Usuario}:{_config.ClaveAplicacion}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
        else
        {
            _logger.LogWarning("Credenciales de Bitbucket no configuradas.");
        }
    }
}
