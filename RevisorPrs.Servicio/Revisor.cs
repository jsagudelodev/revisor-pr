using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// Marcas que delimitan el material escrito por quien abre el pull request. Existen
    /// para que el modelo pueda separar sus instrucciones del contenido a revisar: el
    /// título, la descripción y el diff los controla el autor del PR.
    /// </summary>
    private const string InicioMaterial = "<<<MATERIAL_A_REVISAR>>>";
    private const string FinMaterial = "<<<FIN_MATERIAL_A_REVISAR>>>";

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
    /// Valida la configuración del LLM al arrancar: si falta Endpoint o Modelo,
    /// el servicio debe fallar FUERTE y con mensaje accionable (regla RV.12).
    /// La ClaveApi NO es obligatoria en arranque: sin ella, la primera revisión
    /// fallará con un 401 del proveedor, que también es accionable.
    /// </summary>
    public static void ValidarConfiguracion(ConfiguracionLlm configuracion)
    {
        if (configuracion is null)
        {
            throw new InvalidOperationException(
                "Falta la sección 'Llm' en la configuración. Añade 'Llm: { Endpoint, Modelo, ClaveApi, SeveridadMinima }' al appsettings.json.");
        }

        if (string.IsNullOrWhiteSpace(configuracion.Endpoint))
        {
            throw new InvalidOperationException(
                "Llm.Endpoint está vacío. Define la URL del proveedor en appsettings.json → Llm.Endpoint (por ejemplo, https://api.mistral.ai/v1/chat/completions).");
        }

        if (string.IsNullOrWhiteSpace(configuracion.Modelo))
        {
            throw new InvalidOperationException(
                $"Llm.Modelo está vacío (Endpoint = '{configuracion.Endpoint}'). Define el nombre del modelo en appsettings.json → Llm.Modelo (por ejemplo, 'mistral-small-latest').");
        }
    }

    /// <summary>
    /// Mensaje explícito que se usa en el reintento para forzar al LLM a devolver
    /// ÚNICAMENTE un JSON válido (sin prosa alrededor).
    /// </summary>
    private const string MensajeReintento =
        "Tu respuesta anterior no fue un JSON válido. Responde ÚNICAMENTE con un JSON válido que cumpla el formato pedido, sin texto antes ni después.";

    public async Task<ResultadoRevision> RevisarAsync(
        string diff,
        ContextoRevision? contexto = null,
        CancellationToken token = default)
    {
        if (diff is null)
        {
            throw new ArgumentNullException(nameof(diff));
        }

        string mensajeUsuario = ComponerMensajeUsuario(diff, contexto);

        // Primer intento con el prompt habitual.
        var (contenidoCrudo, consumo) = await EnviarAlLlmAsync(mensajeUsuario, PromptRevision.Mensaje, token);
        var (ok, json, motivo) = IntentarExtraerJson(contenidoCrudo);
        if (ok)
        {
            // Si el JSON se recuperó de un truncado (motivo no nulo), conservamos los
            // hallazgos válidos y registramos cuántos se perdieron: NO reintentamos,
            // porque reintentar reproduciría los mismos hallazgos y volvería a cortar.
            if (motivo is not null)
            {
                _logger.LogWarning(
                    "La respuesta del LLM llegó truncada. {Motivo}. Se conservan los hallazgos completos.",
                    motivo);
                return new ResultadoRevision(
                    Exito: true,
                    Hallazgos: ParsearHallazgos(json!),
                    Motivo: motivo,
                    Consumo: consumo);
            }
            return ResultadoRevision.Ok(ParsearHallazgos(json!), consumo);
        }

        // Un único reintento pidiendo explícitamente solo JSON.
        _logger.LogWarning(
            "La respuesta del LLM no es JSON válido ({Motivo}). Se reintenta una vez pidiendo solo JSON.",
            motivo);

        // El reintento se paga igual que el primer intento: el consumo se suma.
        var (contenidoReintento, consumoReintento) =
            await EnviarAlLlmAsync(mensajeUsuario, MensajeReintento, token);
        contenidoCrudo = contenidoReintento;
        consumo += consumoReintento;

        (ok, json, motivo) = IntentarExtraerJson(contenidoCrudo);
        if (ok)
        {
            if (motivo is not null)
            {
                _logger.LogWarning(
                    "La respuesta del LLM llegó truncada tras el reintento. {Motivo}. Se conservan los hallazgos completos.",
                    motivo);

                // El motivo se conserva igual que en el primer intento. Antes se
                // descartaba aqui, ocultando informacion de diagnostico justo en el
                // camino mas raro, que es cuando hace falta.
                return new ResultadoRevision(
                    Exito: true,
                    Hallazgos: ParsearHallazgos(json!),
                    Motivo: motivo,
                    Consumo: consumo);
            }

            return ResultadoRevision.Ok(ParsearHallazgos(json!), consumo);
        }

        // Segundo intento también inválido: marcamos el PR como FALLIDO sin hallazgos
        // para que NUNCA se publique basura.
        _logger.LogError(
            "La respuesta del LLM no es JSON válido tras un reintento ({Motivo}). Se marca el PR como fallido.",
            motivo);

        return ResultadoRevision.Fallo(
            $"La respuesta del LLM no es JSON válido tras un reintento ({motivo}).", consumo);
    }

    /// <summary>Sustituto visible de una marca delimitadora que venía en el material.</summary>
    private const string MarcaNeutralizada = "[marca de bloque omitida]";

    /// <summary>
    /// Quita del texto del autor las marcas delimitadoras, para que no pueda cerrar el
    /// bloque antes de tiempo y colar instrucciones fuera de él.
    /// </summary>
    /// <remarks>
    /// Es el ataque evidente contra nuestro propio esquema: si el título o el diff
    /// contienen la marca de cierre, todo lo que viniera después quedaría presentado al
    /// modelo como si lo dijéramos nosotros.
    /// </remarks>
    private static string Neutralizar(string? texto)
    {
        if (string.IsNullOrEmpty(texto))
        {
            return string.Empty;
        }

        return texto
            .Replace(FinMaterial, MarcaNeutralizada, StringComparison.OrdinalIgnoreCase)
            .Replace(InicioMaterial, MarcaNeutralizada, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Arma el mensaje de usuario: primero la intención declarada del pull request, si se
    /// conoce, y después el diff.
    /// </summary>
    /// <remarks>
    /// Sin la intención el modelo no puede distinguir un cambio deliberado que el autor
    /// explica de un descuido, y esa confusión es una fuente grande de falsos positivos.
    ///
    /// Todo el material va entre marcas de bloque y pasa por <see cref="Neutralizar"/>,
    /// porque lo escribe quien abre el pull request. Delimitar es solo la primera capa:
    /// la que de verdad acota el daño es comprobar la SALIDA del modelo —que un hallazgo
    /// hable de un archivo del diff y que su severidad esté en el juego conocido—, porque
    /// no depende de que el modelo obedezca.
    /// </remarks>
    private static string ComponerMensajeUsuario(string diff, ContextoRevision? contexto)
    {
        var mensaje = new StringBuilder();

        // Las convenciones del equipo van PRIMERO: son el criterio con el que hay que
        // juzgar todo lo demás. Y van fuera de las marcas de material a revisar porque
        // no las escribe el autor del pull request: viven en la rama de destino y han
        // pasado por la revisión del equipo para llegar ahí.
        if (contexto is not null && contexto.TieneGuia)
        {
            mensaje.Append("Convenciones acordadas por el equipo de este repositorio. ");
            mensaje.Append("Aplícalas como criterio de revisión.\n\n");
            mensaje.Append(contexto.Guia!.Trim()).Append("\n\n");
        }

        if (contexto is not null && contexto.TieneAlgo)
        {
            mensaje.Append("Intención declarada por el autor del pull request.\n");
            mensaje.Append(InicioMaterial).Append('\n');

            string titulo = Neutralizar(contexto.Titulo).Trim();
            if (titulo.Length > 0)
            {
                mensaje.Append("Título: ").Append(titulo).Append('\n');
            }

            string descripcion = Neutralizar(contexto.Descripcion).Trim();
            if (descripcion.Length > 0)
            {
                mensaje.Append("Descripción:\n").Append(descripcion).Append('\n');
            }

            mensaje.Append(FinMaterial).Append("\n\n");
        }

        mensaje.Append("Analiza el siguiente diff y devuelve los hallazgos en JSON.\n");
        mensaje.Append(InicioMaterial).Append('\n');
        mensaje.Append(Neutralizar(diff)).Append('\n');
        mensaje.Append(FinMaterial);

        return mensaje.ToString();
    }

    private async Task<(string Contenido, ConsumoTokens Consumo)> EnviarAlLlmAsync(
        string mensajeUsuario, string mensajeSistema, CancellationToken token)
    {
        var cuerpo = new
        {
            model = _config.Modelo,
            messages = new object[]
            {
                new { role = "system", content = mensajeSistema },
                new { role = "user", content = mensajeUsuario },
            },
            response_format = new { type = "json_object" },
            max_tokens = _config.MaxTokensRespuesta,
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
            mensajeUsuario.Length);

        using var response = await _httpClient.SendAsync(request, token);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        string contenido = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;

        return (contenido, LeerConsumo(doc.RootElement));
    }

    /// <summary>
    /// Lee el bloque "usage" que devuelven las APIs compatibles con chat completions.
    /// </summary>
    /// <remarks>
    /// Es opcional a propósito: un proveedor que no lo devuelva no puede tumbar la
    /// revisión. Sin este dato no se puede informar del coste, que es peor que tenerlo
    /// pero mucho mejor que fallar.
    /// </remarks>
    private static ConsumoTokens LeerConsumo(JsonElement raiz)
    {
        if (!raiz.TryGetProperty("usage", out var uso) || uso.ValueKind != JsonValueKind.Object)
        {
            return ConsumoTokens.Ninguno;
        }

        return new ConsumoTokens(
            LeerEntero(uso, "prompt_tokens"), LeerEntero(uso, "completion_tokens"));
    }

    private static int LeerEntero(JsonElement objeto, string propiedad)
        => objeto.TryGetProperty(propiedad, out var valor)
            && valor.ValueKind == JsonValueKind.Number
            && valor.TryGetInt32(out int n)
                ? n
                : 0;

    /// <summary>
    /// Intenta extraer un JSON válido de la respuesta cruda del LLM.
    /// Hay DOS caminos bien diferenciados:
    ///
    ///   1. PROSA / respuesta que NO es JSON en absoluto (con o sin bloque markdown):
    ///      se considera un fallo de formato normal y se devuelve (false, null, motivo)
    ///      para que el llamador (RevisarAsync) decida si reintenta. Esto es RV.10.
    ///
    ///   2. RESPUESTA TRUNCADA que EMPIEZA como JSON (un '{' al principio) pero llega
    ///      cortada a mitad de un valor o de un objeto: NO es prosa, es un agotamiento
    ///      de max_tokens. Aquí NO se reintenta (repetiría el mismo corte): se recuperan
    ///      los hallazgos completos que se hayan podido cerrar y se devuelve
    ///      (true, json, motivo) para que el llamador los publique como éxito parcial.
    ///      Esto es RV.10b.
    ///
    /// La distinción clave está en <see cref="texto"/>.StartsWith("{"): si la respuesta
    /// empieza por '{' NO es prosa, por mucho que no parse entero. Si no empieza por
    /// '{', es prosa (posiblemente envuelta en markdown) y entra al camino de reintento.
    /// </summary>
    private static (bool Ok, string? Json, string? Motivo) IntentarExtraerJson(string contenidoCrudo)
    {
        if (string.IsNullOrWhiteSpace(contenidoCrudo))
        {
            return (false, null, "respuesta vacía");
        }

        var texto = contenidoCrudo.Trim();

        // Caso RV.10b: la respuesta EMPIEZA por '{', así que NO es prosa. Puede estar
        // completa o truncada a mitad de un valor. Probamos primero el parseo directo:
        // si funciona, es el caso normal y devolvemos (true, json, null) como siempre.
        // Si NO funciona, intentamos RECUPERAR hallazgos completos del truncado: este
        // camino es el de RECUPERACION y nunca devuelve (false) — o devuelve hallazgos
        // parciales o devuelve un JSON vacío con el motivo claro. La idea es no
        // reintentar: reintentar reproduciría exactamente los mismos hallazgos y volvería
        // a cortar en el mismo sitio.
        if (texto.StartsWith("{", StringComparison.Ordinal))
        {
            if (EsJsonValido(texto))
            {
                return (true, texto, null);
            }

            var (recuperado, motivoTruncado) = IntentarRecuperarJsonTruncado(texto);
            if (recuperado is not null)
            {
                return (true, recuperado, motivoTruncado);
            }

            // JSON truncado del que no se ha podido recuperar ni un hallazgo completo.
            // Devolvemos igualmente (true, ...) con un JSON vacío y motivo informativo:
            // el PR queda marcado como exitoso pero sin hallazgos, y NUNCA se reintenta.
            return (true, "{\"hallazgos\":[]}", "JSON truncado sin hallazgos recuperables");
        }

        // Camino RV.10: la respuesta NO empieza por '{', así que es prosa (puede llevar
        // un JSON envuelto en un bloque markdown ```json ... ```, que es la forma más
        // común y NO se considera error). Si tiene JSON válido (limpio o envuelto), se
        // acepta SIN reintentar. Si no, se devuelve (false, ...) para que RevisarAsync
        // reintente una vez.
        var textoConBloque = ExtraerJsonDeBloqueMarkdown(texto);
        if (!ReferenceEquals(textoConBloque, texto) && EsJsonValido(textoConBloque))
        {
            return (true, textoConBloque, null);
        }

        if (EsJsonValido(texto))
        {
            return (true, texto, null);
        }

        return (false, null, "la respuesta no contiene un JSON válido");
    }

    /// <summary>
    /// Dado un texto que empieza por '{' pero NO parsea como JSON, recupera los
    /// hallazgos completos que hubiera antes del corte. Devuelve (json, motivo) si
    /// encuentra algo aprovechable; (null, null) si no.
    /// </summary>
    /// <remarks>
    /// Un solo recorrido de izquierda a derecha, llevando la pila de llaves y corchetes
    /// abiertos y respetando las cadenas y sus escapes. Se anota la posicion del ultimo
    /// elemento del array "hallazgos" que llego a cerrarse, y al final se cierra la
    /// estructura con EXACTAMENTE los cierres que quedan pendientes en la pila.
    ///
    /// La version anterior recorria de derecha a izquierda probando seis sufijos fijos
    /// en cada posible punto de corte, y reparseaba el prefijo entero en cada prueba: el
    /// coste crecia con el cuadrado del tamano, justo en el caso de una respuesta larga
    /// que ya venia mal. Ademas los sufijos eran adivinanzas; con la pila, el cierre es
    /// el correcto por construccion.
    /// </remarks>
    private static (string? Json, string? Motivo) IntentarRecuperarJsonTruncado(string texto)
    {
        int inicioArray = LocalizarArrayHallazgos(texto);
        if (inicioArray < 0)
        {
            return (null, null);
        }

        var pila = new Stack<char>();
        bool enCadena = false;
        bool escapado = false;

        // Profundidad de la pila justo dentro del array: un elemento se ha cerrado
        // cuando volvemos a este nivel tras un '}'.
        int profundidadElemento = -1;
        int finUltimoElemento = -1;
        int hallazgosCompletos = 0;

        for (int i = 0; i < texto.Length; i++)
        {
            char c = texto[i];

            if (enCadena)
            {
                if (escapado) { escapado = false; }
                else if (c == '\\') { escapado = true; }
                else if (c == '"') { enCadena = false; }
                continue;
            }

            switch (c)
            {
                case '"':
                    enCadena = true;
                    break;

                case '{':
                case '[':
                    pila.Push(c);
                    if (i == inicioArray)
                    {
                        profundidadElemento = pila.Count;
                    }
                    break;

                case '}':
                case ']':
                    if (pila.Count == 0)
                    {
                        // Estructura incoherente: no hay nada de fiar aqui.
                        return (null, null);
                    }
                    pila.Pop();
                    if (c == '}' && profundidadElemento > 0 && pila.Count == profundidadElemento)
                    {
                        finUltimoElemento = i;
                        hallazgosCompletos++;
                    }
                    break;
            }
        }

        if (finUltimoElemento < 0)
        {
            // El corte llego antes de cerrar ni un solo hallazgo.
            return (null, null);
        }

        // Se recorta justo tras el ultimo hallazgo entero y se cierra lo que quede
        // abierto. Como la pila esta calculada, el cierre es exacto y no hay que
        // adivinar combinaciones de corchetes.
        var recuperado = new StringBuilder(texto, 0, finUltimoElemento + 1, texto.Length + 8);
        foreach (char abierto in RecalcularPendientes(texto, finUltimoElemento))
        {
            recuperado.Append(abierto == '{' ? '}' : ']');
        }

        string candidato = recuperado.ToString();
        if (!EsJsonValido(candidato))
        {
            return (null, null);
        }

        return (candidato, $"JSON truncado: {hallazgosCompletos} hallazgo(s) recuperado(s) antes del corte");
    }

    /// <summary>
    /// Devuelve, de dentro hacia fuera, los delimitadores que siguen abiertos tras el
    /// caracter indicado.
    /// </summary>
    private static List<char> RecalcularPendientes(string texto, int hasta)
    {
        var pila = new Stack<char>();
        bool enCadena = false;
        bool escapado = false;

        for (int i = 0; i <= hasta; i++)
        {
            char c = texto[i];

            if (enCadena)
            {
                if (escapado) { escapado = false; }
                else if (c == '\\') { escapado = true; }
                else if (c == '"') { enCadena = false; }
                continue;
            }

            if (c == '"') { enCadena = true; }
            else if (c == '{' || c == '[') { pila.Push(c); }
            else if ((c == '}' || c == ']') && pila.Count > 0) { pila.Pop(); }
        }

        return pila.ToList();
    }

    /// <summary>
    /// Posicion del '[' que abre el array "hallazgos", o -1 si no aparece.
    /// </summary>
    private static int LocalizarArrayHallazgos(string texto)
    {
        int clave = texto.IndexOf("\"hallazgos\"", StringComparison.OrdinalIgnoreCase);
        if (clave < 0)
        {
            return -1;
        }

        for (int i = clave + "\"hallazgos\"".Length; i < texto.Length; i++)
        {
            char c = texto[i];
            if (c == '[')
            {
                return i;
            }
            if (!char.IsWhiteSpace(c) && c != ':')
            {
                return -1;
            }
        }

        return -1;
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