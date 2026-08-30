using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using RevisorPrs.Servicio;

namespace RevisorPrs.Tests;

/// <summary>
/// Pruebas del <see cref="Revisor"/>: verifican la traducción de la respuesta del LLM
/// y que la clave de API nunca aparece en los logs.
/// </summary>
public class RevisorTests
{
    private const string ClaveSecreta = "clave-secreta-que-no-debe-aparecer-en-el-log-1234567890";

    [Fact]
    public async Task RevisarAsync_ConRespuestaValida_DevuelveLosHallazgos()
    {
        // El LLM devuelve un JSON con la lista de hallazgos en la propiedad "hallazgos".
        var contenidoLlm = """
            {
              "hallazgos": [
                {
                  "Archivo": "src/Calculadora.cs",
                  "Linea": 42,
                  "Severidad": "warning",
                  "Resumen": "Posible división por cero",
                  "Detalle": "El método Dividir no valida que el divisor sea distinto de cero."
                },
                {
                  "Archivo": "src/Utilidades.cs",
                  "Linea": null,
                  "Severidad": "info",
                  "Resumen": "Falta documentación XML",
                  "Detalle": "Añadir comentarios /// a los métodos públicos para mejorar la mantenibilidad."
                }
              ]
            }
            """;

        var handler = new HandlerLlmFalso(contenidoLlm);
        var http = new HttpClient(handler);
        var config = Options.Create(new ConfiguracionLlm
        {
            Endpoint = "https://llm.ejemplo.test/v1/chat/completions",
            Modelo = "modelo-de-prueba",
            ClaveApi = ClaveSecreta,
        });
        var registrador = new RegistradorFalso<Revisor>();
        var revisor = new Revisor(http, config, registrador);

        var diff = "diff --git a/src/Calculadora.cs b/src/Calculadora.cs\n+var x = 1/0;";
        var resultados = await revisor.RevisarAsync(diff);

        Assert.Equal(2, resultados.Count);

        Assert.Equal("src/Calculadora.cs", resultados[0].Archivo);
        Assert.Equal(42, resultados[0].Linea);
        Assert.Equal("warning", resultados[0].Severidad);
        Assert.Equal("Posible división por cero", resultados[0].Resumen);
        Assert.Equal(
            "El método Dividir no valida que el divisor sea distinto de cero.",
            resultados[0].Detalle);

        Assert.Equal("src/Utilidades.cs", resultados[1].Archivo);
        Assert.Null(resultados[1].Linea);
        Assert.Equal("info", resultados[1].Severidad);
        Assert.Equal("Falta documentación XML", resultados[1].Resumen);
        Assert.Equal(
            "Añadir comentarios /// a los métodos públicos para mejorar la mantenibilidad.",
            resultados[1].Detalle);

        // Comprobamos también que el handler recibió la petición con la cabecera Bearer correcta.
        Assert.Equal(HttpStatusCode.OK, handler.UltimaRespuesta?.StatusCode);
        Assert.NotNull(handler.UltimaPeticion);
        Assert.True(handler.PeticionTeniaBearer, "La petición debe llevar cabecera Authorization: Bearer.");
    }

    [Fact]
    public async Task RevisarAsync_NoEscribeLaClaveEnElLog()
    {
        var contenidoLlm = """{"hallazgos": []}""";
        var handler = new HandlerLlmFalso(contenidoLlm);
        var http = new HttpClient(handler);
        var config = Options.Create(new ConfiguracionLlm
        {
            Endpoint = "https://llm.ejemplo.test/v1/chat/completions",
            Modelo = "modelo-de-prueba",
            ClaveApi = ClaveSecreta,
        });
        var registrador = new RegistradorFalso<Revisor>();
        var revisor = new Revisor(http, config, registrador);

        await revisor.RevisarAsync("diff --git a/x b/x\n+cambio");

        // La clave no debe aparecer en ningún mensaje capturado por el logger.
        Assert.DoesNotContain(ClaveSecreta, string.Join("\n", registrador.Mensajes));

        // Tampoco debe aparecer en la URL ni en la petición HTTP que salió hacia el LLM.
        var peticionComoTexto = LeerPeticionComoTexto(handler);
        Assert.DoesNotContain(ClaveSecreta, peticionComoTexto);
        Assert.DoesNotContain(ClaveSecreta, handler.UltimaUrlAbsoluta);
    }

    private static string LeerPeticionComoTexto(HandlerLlmFalso handler) => handler.UltimoCuerpoPeticion;

    /// <summary>
    /// Handler de <see cref="HttpMessageHandler"/> que simula la respuesta del LLM
    /// y captura la última petición para inspección.
    /// </summary>
    private sealed class HandlerLlmFalso : HttpMessageHandler
    {
        private readonly string _respuestaLlm;

        public HandlerLlmFalso(string respuestaLlm)
        {
            _respuestaLlm = respuestaLlm;
        }

        public HttpRequestMessage? UltimaPeticion { get; private set; }
        public HttpResponseMessage? UltimaRespuesta { get; private set; }
        public bool PeticionTeniaBearer { get; private set; }
        public string UltimaUrlAbsoluta { get; private set; } = string.Empty;
        public string UltimoCuerpoPeticion { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UltimaPeticion = request;
            UltimaUrlAbsoluta = request.RequestUri?.ToString() ?? string.Empty;
            ComprobarCabeceraBearer(request);

            // Envelopamos la respuesta del LLM en el formato estándar de chat completions.
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteStartArray("choices");
                writer.WriteStartObject();
                writer.WriteStartObject("message");
                writer.WriteString("role", "assistant");
                writer.WriteString("content", _respuestaLlm);
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
            UltimaRespuesta = respuesta;
            return Task.FromResult(respuesta);
        }

        private void ComprobarCabeceraBearer(HttpRequestMessage request)
        {
            var cabecera = request.Headers.Authorization;
            PeticionTeniaBearer = cabecera is not null
                && string.Equals(cabecera.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase);
        }
    }
}