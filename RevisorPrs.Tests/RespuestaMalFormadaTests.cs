using System;
using System.Collections.Generic;
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
/// Pruebas del item RV.10: respuesta mal formada del LLM.
///   1. JSON limpio → se acepta en el primer intento (UNA llamada).
///   2. JSON envuelto en bloque markdown ```json … ``` → se acepta SIN reintentar (UNA llamada).
///   3. Prosa en los dos intentos → se reintenta una sola vez y se marca FALLIDO sin hallazgos
///      (DOS llamadas exactas).
/// </summary>
public class RespuestaMalFormadaTests
{
    [Fact]
    public async Task RevisarAsync_ConJsonLimpio_DevuelveHallazgosSinReintentar()
    {
        var contenidoLlm = """
            {
              "hallazgos": [
                {
                  "Archivo": "src/Ejemplo.cs",
                  "Linea": 7,
                  "Severidad": "warning",
                  "Resumen": "Uso de var con tipo primitivo",
                  "Detalle": "Preferir tipo explícito en APIs públicas."
                }
              ]
            }
            """;

        var handler = new HandlerSecuencial(new[] { contenidoLlm });
        var revisor = CrearRevisor(handler);

        var resultado = await revisor.RevisarAsync("diff --git a/x b/x\n+cambio");

        Assert.True(resultado.Exito, "Con JSON limpio la revisión debe tener éxito.");
        Assert.Null(resultado.Motivo);
        Assert.Single(resultado.Hallazgos);
        Assert.Equal("src/Ejemplo.cs", resultado.Hallazgos[0].Archivo);
        Assert.Equal(7, resultado.Hallazgos[0].Linea);
        Assert.Equal(1, handler.Llamadas);
    }

    [Fact]
    public async Task RevisarAsync_ConJsonEnvueltoEnMarkdown_LoAceptaSinReintentar()
    {
        // JSON envuelto en un bloque markdown: es la forma más común y NO debe reintentar.
        var contenidoLlm =
            "Aquí tienes el resultado:\n\n```json\n" +
            "{\"hallazgos\":[{\"Archivo\":\"src/A.cs\",\"Linea\":1,\"Severidad\":\"info\",\"Resumen\":\"OK\",\"Detalle\":\"Sin problemas\"}]}\n" +
            "```\n\nListo.";

        var handler = new HandlerSecuencial(new[] { contenidoLlm });
        var revisor = CrearRevisor(handler);

        var resultado = await revisor.RevisarAsync("diff --git a/x b/x\n+cambio");

        Assert.True(resultado.Exito, "Un JSON envuelto en markdown debe aceptarse sin reintentar.");
        Assert.Null(resultado.Motivo);
        Assert.Single(resultado.Hallazgos);
        Assert.Equal("src/A.cs", resultado.Hallazgos[0].Archivo);
        Assert.Equal(1, handler.Llamadas);
    }

    [Fact]
    public async Task RevisarAsync_ConProsaEnLosDosIntentos_MarcaFallidoSinHallazgos()
    {
        // Dos respuestas que NO son JSON válido. Se esperan exactamente DOS llamadas:
        // la original y el reintento. Tras el segundo fallo, el resultado debe marcar el PR
        // como FALLIDO con un motivo y SIN hallazgos (nunca se publica basura en un PR).
        var prosaInvalida = "Lo siento, no puedo ayudarte con eso. Soy un modelo de texto.";

        var handler = new HandlerSecuencial(new[] { prosaInvalida, prosaInvalida });
        var revisor = CrearRevisor(handler);

        var resultado = await revisor.RevisarAsync("diff --git a/x b/x\n+cambio");

        Assert.False(resultado.Exito, "Tras dos respuestas inválidas, la revisión debe marcarse como fallida.");
        Assert.NotNull(resultado.Motivo);
        Assert.Contains("JSON", resultado.Motivo!);
        Assert.Empty(resultado.Hallazgos);
        Assert.Equal(2, handler.Llamadas);
    }

    private static IRevisor CrearRevisor(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        var config = Options.Create(new ConfiguracionLlm
        {
            Endpoint = "https://llm.ejemplo.test/v1/chat/completions",
            Modelo = "modelo-de-prueba",
            ClaveApi = "clave-de-prueba",
        });
        return new Revisor(http, config, NullLogger<Revisor>.Instance);
    }

    /// <summary>
    /// Handler que devuelve una secuencia de respuestas preestablecidas y
    /// cuenta el número de llamadas realizadas.
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