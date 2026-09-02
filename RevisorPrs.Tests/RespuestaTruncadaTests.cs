using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RevisorPrs.Servicio;

namespace RevisorPrs.Tests;

/// <summary>
/// Pruebas del item RV.10b: la respuesta del LLM puede llegar truncada a mitad
/// de un objeto cuando se gasta el tope de tokens. En ese caso el JSON está mal
/// formado, pero los hallazgos completos que se hayan escrito antes del corte
/// deben aprovecharse: NO se tira la revisión entera.
/// </summary>
public class RespuestaTruncadaTests
{
    [Fact]
    public async Task RevisarAsync_ConJsonTruncadoEnMitadDeObjeto_RecuperaHallazgosCompletos()
    {
        // El LLM empezó a escribir hallazgos pero el segundo objeto se quedó a medias
        // (le falta el cierre '}' y parte de los campos). El primero está COMPLETO
        // (con sus campos y su '}' de cierre) y por tanto debe ser recuperable.
        // Primer hallazgo COMPLETO y cerrado, segundo CORTO a mitad de un valor
        // de cadena (sin '}' de cierre). El revisor debe cortar donde pueda
        // parsear un JSON válido y devolver solo el primer hallazgo.
        var contenidoLlm =
            "{\"hallazgos\":[" +
            "{\"Archivo\":\"src/A.cs\",\"Linea\":10,\"Severidad\":\"error\",\"Resumen\":\"NullReference potencial\",\"Detalle\":\"Foo no valida el argumento\"}," +
            "{\"Archivo\":\"src/B.cs\",\"Linea\":42,\"Severidad\":\"warning\",\"Resumen\":\"Catch vacío\",\"Detalle\":\"El bloque catch no regis";

        var handler = new HandlerSecuencial(new[] { contenidoLlm });
        var revisor = CrearRevisor(handler, maxTokensRespuesta: 200);

        var resultado = await revisor.RevisarAsync("diff --git a/x b/x\n+cambio");

        Assert.True(resultado.Exito, "Una respuesta truncada debe considerarse ÉXITO parcial con los hallazgos recuperables.");
        Assert.NotNull(resultado.Motivo);
        Assert.Contains("truncado", resultado.Motivo!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1", resultado.Motivo!);
        Assert.Single(resultado.Hallazgos);
        Assert.Equal("src/A.cs", resultado.Hallazgos[0].Archivo);
        Assert.Equal(10, resultado.Hallazgos[0].Linea);
        Assert.Equal("error", resultado.Hallazgos[0].Severidad);
        Assert.Equal("NullReference potencial", resultado.Hallazgos[0].Resumen);
        // El hallazgo incompleto NO debe publicarse: el revisor solo devuelve los
        // objetos que cerró correctamente.
        Assert.DoesNotContain(resultado.Hallazgos, h => h.Archivo == "src/B.cs");
        Assert.Equal(1, handler.Llamadas);
    }

    [Fact]
    public async Task RevisarAsync_ConJsonEntero_NoCambiaComportamiento()
    {
        // Respuesta perfectamente válida: dos hallazgos completos. El cambio de RV.10b
        // no debe afectar al caso normal: éxito sin motivo, dos hallazgos, una sola
        // llamada.
        var contenidoLlm = """
            {
              "hallazgos": [
                {
                  "Archivo": "src/A.cs",
                  "Linea": 1,
                  "Severidad": "info",
                  "Resumen": "Sin hallazgos",
                  "Detalle": "El archivo está limpio."
                },
                {
                  "Archivo": "src/B.cs",
                  "Linea": 7,
                  "Severidad": "warning",
                  "Resumen": "Variable no usada",
                  "Detalle": "La variable x se asigna pero no se lee."
                }
              ]
            }
            """;

        var handler = new HandlerSecuencial(new[] { contenidoLlm });
        var revisor = CrearRevisor(handler, maxTokensRespuesta: 8000);

        var resultado = await revisor.RevisarAsync("diff --git a/x b/x\n+cambio");

        Assert.True(resultado.Exito);
        Assert.Null(resultado.Motivo);
        Assert.Equal(2, resultado.Hallazgos.Count);
        Assert.Equal("src/A.cs", resultado.Hallazgos[0].Archivo);
        Assert.Equal("src/B.cs", resultado.Hallazgos[1].Archivo);
        Assert.Equal(1, handler.Llamadas);
    }

    private static IRevisor CrearRevisor(HttpMessageHandler handler, int maxTokensRespuesta)
    {
        var http = new HttpClient(handler);
        var config = Options.Create(new ConfiguracionLlm
        {
            Endpoint = "https://llm.ejemplo.test/v1/chat/completions",
            Modelo = "modelo-de-prueba",
            ClaveApi = "clave-de-prueba",
            MaxTokensRespuesta = maxTokensRespuesta,
        });
        return new Revisor(http, config, NullLogger<Revisor>.Instance);
    }

    /// <summary>
    /// Handler que devuelve una secuencia de respuestas preestablecidas y
    /// cuenta el número de llamadas realizadas. Reutiliza el patrón de
    /// RespuestaMalFormadaTests.
    /// </summary>
    private sealed class HandlerSecuencial : HttpMessageHandler
    {
        private readonly string[] _respuestas;

        public HandlerSecuencial(string[] respuestas)
        {
            _respuestas = respuestas;
        }

        public int Llamadas { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Llamadas >= _respuestas.Length)
            {
                throw new InvalidOperationException(
                    $"El handler recibió más llamadas de las esperadas ({Llamadas + 1}).");
            }

            var contenido = _respuestas[Llamadas];
            Llamadas++;

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteStartArray("choices");
                writer.WriteStartObject();
                writer.WriteStartObject("message");
                writer.WriteString("role", "assistant");
                writer.WriteString("content", contenido);
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            var respuesta = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(stream.ToArray()),
            };
            respuesta.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Task.FromResult(respuesta);
        }
    }
}