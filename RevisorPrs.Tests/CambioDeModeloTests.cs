using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using RevisorPrs.Servicio;

namespace RevisorPrs.Tests;

/// <summary>
/// Pruebas del requisito RV.12: cambiar de proveedor y de modelo SOLO tocando la
/// configuración, sin recompilar ni cambiar código.
/// </summary>
public class CambioDeModeloTests
{
    /// <summary>
    /// Cambia endpoint y modelo únicamente por configuración y comprueba que la URL
    /// de la petición y el campo "model" del cuerpo enviado reflejan los nuevos valores.
    /// </summary>
    [Fact]
    public async Task Revisor_ConOtroEndpointYModelo_EnviaLaPeticionAlNuevoDestino()
    {
        var handler = new HandlerLlmCambioModelo("""{"hallazgos": []}""");
        var http = new HttpClient(handler);

        var config = Options.Create(new ConfiguracionLlm
        {
            Endpoint = "https://api.otro-proveedor.test/v1/chat/completions",
            Modelo = "modelo-nuevo-2025",
            ClaveApi = "clave-cualquiera",
        });

        var revisor = new Revisor(http, config, new RegistradorFalso<Revisor>());

        var resultado = await revisor.RevisarAsync("diff --git a/x b/x\n+cambio");

        Assert.True(resultado.Exito);

        // La URL de la petición debe coincidir EXACTAMENTE con el Endpoint configurado.
        Assert.Equal(
            "https://api.otro-proveedor.test/v1/chat/completions",
            handler.UltimaUrlAbsoluta);

        // El cuerpo enviado debe llevar el campo "model" con el valor configurado.
        Assert.Contains("\"model\":\"modelo-nuevo-2025\"", handler.UltimoCuerpoPeticion);

        // Y NO debe llevar el modelo antiguo hardcoded en ningún sitio.
        Assert.DoesNotContain("modelo-de-prueba", handler.UltimoCuerpoPeticion);
    }

    /// <summary>
    /// El arranque debe fallar con mensaje accionable si falta el Endpoint,
    /// IGUAL que ya ocurre con la configuración del sondeo (regla RV.1).
    /// </summary>
    [Fact]
    public void Arranque_SinEndpoint_FallaConMensajeAccionable()
    {
        var configuracion = new ConfiguracionLlm
        {
            Endpoint = "",
            Modelo = "modelo-cualquiera",
            ClaveApi = "clave-cualquiera",
        };

        var excepcion = Assert.Throws<InvalidOperationException>(
            () => Revisor.ValidarConfiguracion(configuracion));

        // El mensaje debe mencionar la clave de configuración que el operador debe tocar.
        Assert.Contains("Llm.Endpoint", excepcion.Message);
        Assert.Contains("appsettings.json", excepcion.Message);
    }

    /// <summary>
    /// Handler de <see cref="HttpMessageHandler"/> propio de esta suite: simula la respuesta
    /// del LLM (mismo formato que <c>HandlerLlmFalso</c> de <see cref="RevisorTests"/>)
    /// y captura la última petición para inspeccionar la URL y el cuerpo enviado.
    /// </summary>
    private sealed class HandlerLlmCambioModelo : HttpMessageHandler
    {
        private readonly string _respuestaLlm;

        public HandlerLlmCambioModelo(string respuestaLlm)
        {
            _respuestaLlm = respuestaLlm;
        }

        public string UltimaUrlAbsoluta { get; private set; } = string.Empty;
        public string UltimoCuerpoPeticion { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UltimaUrlAbsoluta = request.RequestUri?.ToString() ?? string.Empty;

            if (request.Content is not null)
            {
                UltimoCuerpoPeticion = await request.Content.ReadAsStringAsync(cancellationToken);
            }

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
            return respuesta;
        }
    }
}