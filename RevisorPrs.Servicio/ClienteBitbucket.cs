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

        try
        {
            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync();
            }
            else
            {
                _logger.LogWarning("Error al obtener diff de Bitbucket: {StatusCode} para {Repo} PR #{Numero}", 
                    (int)response.StatusCode, repositorio, numero);
                return string.Empty;
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Excepción de red al obtener diff de Bitbucket para {Repo} PR #{Numero}", 
                repositorio, numero);
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error inesperado al obtener diff de Bitbucket para {Repo} PR #{Numero}", 
                repositorio, numero);
            return string.Empty;
        }
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