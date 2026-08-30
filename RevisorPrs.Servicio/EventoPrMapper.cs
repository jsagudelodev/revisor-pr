using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace RevisorPrs.Servicio;

/// <summary>
/// Mapea la respuesta cruda de la API de Bitbucket Cloud a EventoPr.
/// Los campos que no se entienden se descartan con log, sin lanzar excepción.
/// </summary>
public class EventoPrMapper
{
    private readonly ILogger<EventoPrMapper> _logger;

    public EventoPrMapper(ILogger<EventoPrMapper> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Convierte un objeto JSON de Bitbucket (representado como JsonElement) a EventoPr.
    /// Devuelve null si falta algún campo esencial o si ocurre un error de mapeo.
    /// </summary>
    public EventoPr? Mapear(JsonElement json)
    {
        try
        {
            // Extraer campos necesarios
            string? repositorio = null;
            int numero = 0;
            string? commit = null;
            string? titulo = null;
            string? rama = null;

            // Repositorio: extraer de links.html.href
            if (json.TryGetProperty("links", out var links) &&
                links.TryGetProperty("html", out var html) &&
                html.TryGetProperty("href", out var href))
            {
                var hrefStr = href.GetString();
                if (!string.IsNullOrEmpty(hrefStr))
                {
                    // Ejemplo: https://bitbucket.org/workspace/repo/pull-requests/123
                    var partes = hrefStr.Split('/');
                    if (partes.Length >= 6 && partes[2] == "bitbucket.org")
                    {
                        var workspace = partes[3];
                        var repo = partes[4];
                        repositorio = $"{workspace}/{repo}";
                    }
                    // También considerar el formato de endpoint de API: https://api.bitbucket.org/2.0/repositories/workspace/repo/pullrequest/123
                    else if (partes.Length >= 8 && partes[2] == "api.bitbucket.org" && partes[3] == "2.0" && partes[4] == "repositories")
                    {
                        var workspace = partes[5];
                        var repo = partes[6];
                        repositorio = $"{workspace}/{repo}";
                    }
                }
            }

            // Número
            if (json.TryGetProperty("id", out var idProp))
            {
                if (idProp.ValueKind == JsonValueKind.Number)
                {
                    numero = idProp.GetInt32();
                }
                else if (idProp.ValueKind == JsonValueKind.String && int.TryParse(idProp.GetString(), out var numFromStr))
                {
                    numero = numFromStr;
                }
            }

            // Commit (del source commit hash)
            if (json.TryGetProperty("source", out var source) &&
                source.TryGetProperty("commit", out var commitObj) &&
                commitObj.TryGetProperty("hash", out var commitHash))
            {
                commit = commitHash.GetString();
            }

            // Título
            if (json.TryGetProperty("title", out var tituloProp))
            {
                titulo = tituloProp.GetString();
            }

            // Rama de destino
            if (json.TryGetProperty("destination", out var destination) &&
                destination.TryGetProperty("branch", out var branchObj) &&
                branchObj.TryGetProperty("name", out var branchName))
            {
                rama = branchName.GetString();
            }

            // Validar que todos los campos esenciales estén presentes
            if (string.IsNullOrWhiteSpace(repositorio) ||
                numero == 0 ||
                string.IsNullOrWhiteSpace(commit) ||
                string.IsNullOrWhiteSpace(titulo) ||
                string.IsNullOrWhiteSpace(rama))
            {
                _logger.LogWarning("No se pudo mapear el PR de Bitbucket a EventoPr. Falta algún campo esencial. Repositorio: {Repositorio}, Número: {Numero}, Commit: {Commit}, Título: {Titulo}, Rama: {Rama}",
                    repositorio ?? "null", numero, commit ?? "null", titulo ?? "null", rama ?? "null");
                return null;
            }

            return new EventoPr(repositorio, numero, commit, titulo, rama);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inesperado al mapear el PR de Bitbucket a EventoPr");
            return null;
        }
    }

    /// <summary>
    /// Convierte una lista de objetos JSON de Bitbucket a una lista de EventoPr.
    /// Descarta los que no se pueden mapear.
    /// </summary>
    public IEnumerable<EventoPr> MapearLista(JsonElement jsonArray)
    {
        var resultado = new List<EventoPr>();
        if (jsonArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in jsonArray.EnumerateArray())
            {
                var evento = Mapear(element);
                if (evento != null)
                {
                    resultado.Add(evento);
                }
            }
        }
        return resultado;
    }
}