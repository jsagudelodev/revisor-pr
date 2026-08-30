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
    }

    public async Task<IEnumerable<EventoPr>> ListarPrsAbiertos(string repositorio)
    {
        var result = new List<EventoPr>();
        string? url = $"https://api.bitbucket.org/2.0/repositories/{repositorio}/pullrequests?state=OPEN";

        while (!string.IsNullOrEmpty(url))
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url!);
            PonerAutenticacionBasica(request);

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

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

            // Obtener el enlace 'next' para paginación
            if (root.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String)
            {
                url = next.GetString();
            }
            else
            {
                url = null; // No hay más páginas
            }
        }

        return result;
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