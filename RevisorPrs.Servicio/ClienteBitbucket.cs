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

    /// <summary>
    /// Valida que la configuración de Bitbucket tiene exactamente UN método de autenticación
    /// relleno. Falla con un mensaje accionable si trae los dos o ninguno.
    /// </summary>
    public static void ValidarConfiguracion(ConfiguracionBitbucket configuracion)
    {
        ArgumentNullException.ThrowIfNull(configuracion);

        bool basicaRellenada = !string.IsNullOrWhiteSpace(configuracion.Usuario)
            && !string.IsNullOrWhiteSpace(configuracion.ClaveAplicacion);
        bool tokenRellenado = !string.IsNullOrWhiteSpace(configuracion.Token);

        switch (configuracion.MetodoAutenticacion)
        {
            case MetodoAutenticacionBitbucket.Basica:
                if (!basicaRellenada)
                {
                    throw new InvalidOperationException(
                        "Configuración de Bitbucket inválida: el método es 'Basica' pero faltan Usuario o ClaveAplicacion. "
                        + "Rellena Bitbucket:Usuario y Bitbucket:ClaveAplicacion, o cambia Bitbucket:MetodoAutenticacion a 'Token' y rellena Bitbucket:Token.");
                }
                if (tokenRellenado)
                {
                    throw new InvalidOperationException(
                        "Configuración de Bitbucket inválida: el método es 'Basica' pero también hay un Token configurado. "
                        + "Quita el campo Bitbucket:Token o cambia Bitbucket:MetodoAutenticacion a 'Token'.");
                }
                break;

            case MetodoAutenticacionBitbucket.Token:
                if (!tokenRellenado)
                {
                    throw new InvalidOperationException(
                        "Configuración de Bitbucket inválida: el método es 'Token' pero falta Bitbucket:Token. "
                        + "Rellena Bitbucket:Token, o cambia Bitbucket:MetodoAutenticacion a 'Basica' y rellena Bitbucket:Usuario y Bitbucket:ClaveAplicacion.");
                }
                if (basicaRellenada)
                {
                    throw new InvalidOperationException(
                        "Configuración de Bitbucket inválida: el método es 'Token' pero también hay Usuario y ClaveAplicacion configurados. "
                        + "Quita Bitbucket:Usuario y Bitbucket:ClaveAplicacion, o cambia Bitbucket:MetodoAutenticacion a 'Basica'.");
                }
                break;

            default:
                throw new InvalidOperationException(
                    $"Configuración de Bitbucket inválida: método de autenticación desconocido '{configuracion.MetodoAutenticacion}'.");
        }
    }

    public async Task<IEnumerable<EventoPr>> ListarPrsAbiertos(string repositorio, CancellationToken cancelacion = default)
    {
        var result = new List<EventoPr>();
        string? url = $"https://api.bitbucket.org/2.0/repositories/{repositorio}/pullrequests?state=OPEN";

        while (!string.IsNullOrEmpty(url))
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url!);
            PonerAutenticacion(request);

            using var response = await EnviarConReintentos(request, repositorio, "listar PRs", cancelacion);

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
                throw new HttpRequestException(
                    $"Respuesta no exitosa al listar PRs en {repositorio}: {codigo}",
                    inner: null,
                    statusCode: response.StatusCode);
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancelacion);
            using var jsonDoc = await JsonDocument.ParseAsync(stream, cancellationToken: cancelacion);
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

    /// <summary>
    /// Descarga el diff de un pull request. Lanza <see cref="HttpRequestException"/> si la
    /// llamada falla, igual que <see cref="ListarPrsAbiertos"/>.
    /// </summary>
    /// <remarks>
    /// Antes devolvía una cadena vacía ante cualquier fallo, y eso confundía dos
    /// situaciones que exigen respuestas opuestas: un pull request sin cambios (no hay
    /// nada que revisar, se da por revisado) y un fallo de la API (hay que reintentar).
    /// El llamador no podía distinguirlas, así que un 500 puntual acababa marcando el PR
    /// como revisado sin hallazgos y esa revisión se perdía para siempre. Ahora el fallo
    /// se propaga y <see cref="EjecutorVuelta"/> lo trata como tal: backoff y reintento.
    /// </remarks>
    public async Task<string> ObtenerDiff(string repositorio, int numero, CancellationToken cancelacion = default)
    {
        if (string.IsNullOrEmpty(repositorio))
        {
            throw new ArgumentException(
                "El repositorio es obligatorio para obtener el diff.", nameof(repositorio));
        }

        // "context" pide a Bitbucket mas lineas alrededor de cada cambio, igual que
        // "git diff -U<n>". Es la forma barata de dar contexto al modelo: viene en la
        // misma respuesta y no obliga a descargar los ficheros completos.
        int contexto = _config.LineasDeContexto >= 0 ? _config.LineasDeContexto : 0;
        string url =
            $"https://api.bitbucket.org/2.0/repositories/{repositorio}/pullrequests/{numero}/diff?context={contexto}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        PonerAutenticacion(request);

        using var response = await EnviarConReintentos(request, repositorio, $"obtener diff PR #{numero}", cancelacion);

        // null = se agotaron los reintentos; EnviarConReintentos ya lo registró como
        // error accionable, aquí solo hace falta que el fallo llegue al llamador.
        if (response is null)
        {
            throw new HttpRequestException(
                $"No se pudo obtener el diff del PR #{numero} en {repositorio}: se agotaron los reintentos.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var codigo = (int)response.StatusCode;
            _logger.LogError(
                "Error accionable: respuesta no exitosa al obtener el diff en {Repo} PR #{Numero}. Código HTTP: {Codigo}",
                repositorio, numero, codigo);
            throw new HttpRequestException(
                $"Respuesta no exitosa al obtener el diff del PR #{numero} en {repositorio}: {codigo}",
                inner: null,
                statusCode: response.StatusCode);
        }

        return await response.Content.ReadAsStringAsync(cancelacion);
    }

    public async Task PublicarComentario(string repositorio, int numero, Hallazgo hallazgo, CancellationToken cancelacion = default)
    {
        if (string.IsNullOrEmpty(repositorio))
        {
            throw new ArgumentException(
                "El repositorio es obligatorio para publicar un comentario.", nameof(repositorio));
        }

        // Un hallazgo con archivo y linea se ancla a esa linea del diff; el resto va
        // como comentario general del pull request.
        bool anclado = !string.IsNullOrEmpty(hallazgo.Archivo) && hallazgo.Linea.HasValue;
        string cuerpo = FormateadorComentario.Componer(hallazgo, anclado);

        object payload = anclado
            ? new
            {
                content = new { raw = cuerpo },
                inline = new { path = hallazgo.Archivo, to = hallazgo.Linea!.Value },
            }
            : new
            {
                content = new { raw = cuerpo },
            };

        await EnviarComentarioAsync(repositorio, numero, payload, cancelacion);
    }

    /// <summary>
    /// Descarga un archivo del repositorio en la referencia indicada (rama o commit).
    /// </summary>
    /// <remarks>
    /// Un 404 NO es un fallo: significa que el repositorio no tiene ese archivo, que es
    /// el caso normal cuando el equipo aún no ha escrito su guía de convenciones. Solo
    /// los demás códigos de error se propagan.
    /// </remarks>
    public async Task<string?> ObtenerArchivo(
        string repositorio,
        string referencia,
        string ruta,
        CancellationToken cancelacion = default)
    {
        if (string.IsNullOrWhiteSpace(repositorio) || string.IsNullOrWhiteSpace(referencia)
            || string.IsNullOrWhiteSpace(ruta))
        {
            return null;
        }

        string url = $"https://api.bitbucket.org/2.0/repositories/{repositorio}/src/{Uri.EscapeDataString(referencia)}/{ruta.TrimStart('/')}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        PonerAutenticacion(request);

        using var response = await EnviarConReintentos(
            request, repositorio, $"obtener {ruta} en {referencia}", cancelacion);

        if (response is null)
        {
            _logger.LogWarning(
                "No se pudo leer {Ruta} en {Repo}@{Referencia}: se agotaron los reintentos. Se revisa sin guía.",
                ruta, repositorio, referencia);
            return null;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "No se pudo leer {Ruta} en {Repo}@{Referencia}. Código HTTP: {Codigo}. Se revisa sin guía.",
                ruta, repositorio, referencia, (int)response.StatusCode);
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancelacion);
    }

    /// <summary>
    /// Envia un comentario ya compuesto al pull request. Compartido por el comentario
    /// de un hallazgo y por el de resumen.
    /// </summary>
    private async Task EnviarComentarioAsync(
        string repositorio,
        int numero,
        object payload,
        CancellationToken cancelacion)
    {
        if (string.IsNullOrEmpty(repositorio))
        {
            throw new ArgumentException(
                "El repositorio es obligatorio para publicar un comentario.", nameof(repositorio));
        }

        string url = $"https://api.bitbucket.org/2.0/repositories/{repositorio}/pullrequests/{numero}/comments";

        var json = JsonSerializer.Serialize(payload);
        var requestMsg = new HttpRequestMessage(HttpMethod.Post, url);
        PonerAutenticacion(requestMsg);
        requestMsg.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await EnviarConReintentos(requestMsg, repositorio, $"publicar comentario PR #{numero}", cancelacion);

        // Volver sin excepción tiene que significar "el comentario está en el PR": el
        // ejecutor lo anota acto seguido como publicado para no repetirlo, y anotar un
        // comentario que en realidad falló lo perdería para siempre.
        if (response is null)
        {
            throw new HttpRequestException(
                $"No se pudo publicar el comentario en el PR #{numero} de {repositorio}: se agotaron los reintentos.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var codigo = (int)response.StatusCode;
            _logger.LogError(
                "Error accionable: respuesta no exitosa al publicar comentario en {Repo} PR #{Numero}. Código HTTP: {Codigo}",
                repositorio, numero, codigo);
            throw new HttpRequestException(
                $"Respuesta no exitosa al publicar comentario en el PR #{numero} de {repositorio}: {codigo}",
                inner: null,
                statusCode: response.StatusCode);
        }
    }

    public Task PublicarComentarioGeneral(
        string repositorio,
        int numero,
        string texto,
        CancellationToken cancelacion = default)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            throw new ArgumentException(
                "El texto del comentario no puede estar vacío.", nameof(texto));
        }

        return EnviarComentarioAsync(repositorio, numero, new { content = new { raw = texto } }, cancelacion);
    }

    /// <summary>
    /// Envía una petición HTTP reintentando ante respuestas 429 y 5xx con espera creciente.
    /// Un 4xx que no sea 429 NO se reintenta. Al agotar el tope, registra un error
    /// accionable con repositorio, PR y código HTTP y devuelve null (NO lanza).
    /// </summary>
    private async Task<HttpResponseMessage?> EnviarConReintentos(
        HttpRequestMessage request,
        string repositorio,
        string operacion,
        CancellationToken cancelacion)
    {
        var intentosMaximos = _config.IntentosMaximos > 0 ? _config.IntentosMaximos : 1;

        HttpResponseMessage? response = null;
        int? ultimoCodigo = null;

        for (int intento = 1; intento <= intentosMaximos; intento++)
        {
            cancelacion.ThrowIfCancellationRequested();

            var intentoRequest = await ClonarPeticionAsync(request, cancelacion);

            try
            {
                response = await _httpClient.SendAsync(intentoRequest, cancelacion);
            }
            catch (OperationCanceledException) when (!cancelacion.IsCancellationRequested)
            {
                // El token NO está cancelado, así que esto no es una parada del servicio:
                // es el tiempo de espera de HttpClient, que se manifiesta también como
                // cancelación. Es un fallo de red y se reintenta como tal. Si el token SÍ
                // estuviera cancelado, la excepción sale y aborta la operación entera.
                _logger.LogWarning(
                    "Se agotó el tiempo de espera al {Operacion} en {Repo} (intento {Intento}/{Max})",
                    operacion, repositorio, intento, intentosMaximos);
                response = null;
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
                    await EsperarEntreReintentos(esperaMs, cancelacion);
                }
                catch (OperationCanceledException) when (!cancelacion.IsCancellationRequested)
                {
                    // Solo alcanzable desde un doble de test que cancele su propia espera.
                    // Una parada real del servicio debe propagarse, no convertirse en
                    // "reintentos agotados", que dispararía el backoff sin motivo.
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

    private static async Task<HttpRequestMessage> ClonarPeticionAsync(HttpRequestMessage original, CancellationToken cancelacion)
    {
        var clon = new HttpRequestMessage(original.Method, original.RequestUri);
        if (original.Content != null)
        {
            // Bufferizamos en bytes para poder releer el contenido en cada reintento
            // (HttpClient dispone el Stream del Content tras enviarlo la primera vez).
            var bytes = await original.Content.ReadAsByteArrayAsync(cancelacion);
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

    /// <summary>
    /// Coloca la cabecera Authorization en la petición según el método configurado.
    /// </summary>
    private void PonerAutenticacion(HttpRequestMessage request)
    {
        switch (_config.MetodoAutenticacion)
        {
            case MetodoAutenticacionBitbucket.Basica:
                if (!string.IsNullOrEmpty(_config.Usuario) && !string.IsNullOrEmpty(_config.ClaveAplicacion))
                {
                    var credenciales = Convert.ToBase64String(
                        Encoding.ASCII.GetBytes($"{_config.Usuario}:{_config.ClaveAplicacion}"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credenciales);
                }
                else
                {
                    _logger.LogWarning("Credenciales de Bitbucket no configuradas.");
                }
                break;

            case MetodoAutenticacionBitbucket.Token:
                if (!string.IsNullOrEmpty(_config.Token))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.Token);
                }
                else
                {
                    _logger.LogWarning("Token de Bitbucket no configurado.");
                }
                break;

            default:
                _logger.LogWarning("Método de autenticación de Bitbucket no soportado.");
                break;
        }
    }
}