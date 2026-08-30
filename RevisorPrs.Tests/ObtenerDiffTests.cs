using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RevisorPrs.Servicio;
using Xunit;

namespace RevisorPrs.Tests;

public class ObtenerDiffTests
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public FakeHttpMessageHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_responses.Count == 0)
            {
                // No more responses, return empty
                var emptyResponse = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"values\":[]}", Encoding.UTF8, "application/json")
                };
                return Task.FromResult(emptyResponse);
            }

            var response = _responses.Dequeue();
            return Task.FromResult(response);
        }
    }

    private static ClienteBitbucket CrearCliente(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.bitbucket.org/")
        };

        var config = Options.Create(new ConfiguracionBitbucket
        {
            Usuario = "testuser",
            ClaveAplicacion = "testpass"
        });

        var logger = NullLogger<ClienteBitbucket>.Instance;
        var traductor = new TraductorEventoPr(NullLogger<TraductorEventoPr>.Instance);

        return new ClienteBitbucket(httpClient, config, logger, traductor);
    }

    [Fact]
    public async Task ObtenerDiff_ConRespuestaCorrecta_DevuelveElDiff()
    {
        // Arrange
        var expectedDiff = @"diff --git a/file.txt b/file.txt
index e69de29..d2ca2fb 100644
--- a/file.txt
+++ b/file.txt
@@ -0,0 +1 @@
+Hello World
";
        var diffResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(expectedDiff, Encoding.UTF8, "text/plain")
        };

        var handler = new FakeHttpMessageHandler(diffResponse);
        var cliente = CrearCliente(handler);

        // Act
        var diff = await cliente.ObtenerDiff("workspace/repo", 123);

        // Assert
        Assert.Equal(expectedDiff, diff);
    }

    [Fact]
    public async Task ObtenerDiff_ConErrorDeApi_DevuelveVacioSinLanzar()
    {
        // Arrange: el cliente reintenta hasta agotar el tope (3) ante 5xx.
        var handler = new FakeHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError),
            new HttpResponseMessage(HttpStatusCode.InternalServerError),
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
        );
        var cliente = CrearCliente(handler);

        // Act
        var diff = await cliente.ObtenerDiff("workspace/repo", 123);

        // Assert
        Assert.Equal(string.Empty, diff);
    }
}