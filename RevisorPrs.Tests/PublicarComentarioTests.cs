using System;
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

public class PublicarComentarioTests
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public readonly List<HttpRequestMessage> Requests = new();

        public FakeHttpMessageHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_responses.Count == 0)
            {
                // No more responses, return empty success
                var emptyResponse = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
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

    private static HttpResponseMessage CreateJsonResponse(object obj, HttpStatusCode status = HttpStatusCode.OK)
    {
        var json = JsonSerializer.Serialize(obj, _jsonOptions);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    [Fact]
    public async Task PublicarComentario_ConArchivoYLinea_EnviaComentarioAnclado()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(
            CreateJsonResponse(new { }) // successful response
        );

        var cliente = CrearCliente(handler);
        var hallazgo = new Hallazgo("src/Foo.cs", 42, "error", "Something went wrong", "Details here");

        // Act
        await cliente.PublicarComentario("workspace/repo", 123, hallazgo);

        // Assert
        Assert.Single(handler.Requests);
        var request = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.bitbucket.org/2.0/repositories/workspace/repo/pullrequests/123/comments", request.RequestUri!.ToString());

        // Ensure content is JSON with inline
        var content = await request.Content!.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("content", out var contentProp));
        Assert.True(contentProp.TryGetProperty("raw", out var rawProp));

        // El comentario lleva severidad, resumen Y detalle. Antes se publicaba solo el
        // resumen: la justificación que el modelo genera no llegaba al pull request.
        string raw = rawProp.GetString()!;
        Assert.Equal("**Error** · Something went wrong\n\nDetails here", raw);

        // Al ir anclado, Bitbucket ya muestra archivo y línea: no se repiten en el texto.
        Assert.DoesNotContain("src/Foo.cs", raw, StringComparison.Ordinal);

        Assert.True(root.TryGetProperty("inline", out var inlineProp));
        Assert.True(inlineProp.TryGetProperty("path", out var pathProp));
        Assert.Equal(hallazgo.Archivo, pathProp.GetString());
        Assert.True(inlineProp.TryGetProperty("to", out var toProp));
        Assert.Equal(hallazgo.Linea!.Value, toProp.GetInt32());
    }

    [Fact]
    public async Task PublicarComentario_SinLinea_EnviaComentarioGeneral()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(
            CreateJsonResponse(new { })
        );

        var cliente = CrearCliente(handler);
        var hallazgo = new Hallazgo("src/Foo.cs", null, "warning", "Maybe something", "Details");

        // Act
        await cliente.PublicarComentario("workspace/repo", 456, hallazgo);

        // Assert
        Assert.Single(handler.Requests);
        var request = handler.Requests[0];
        var content = await request.Content!.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("content", out var contentProp));
        Assert.True(contentProp.TryGetProperty("raw", out var rawProp));

        // Sin ancla, la ubicación va dentro del texto. Y sin línea no se inventa un
        // ":0", que es lo que se publicaba antes.
        Assert.Equal("**Aviso** · `src/Foo.cs` · Maybe something\n\nDetails", rawProp.GetString());

        // Should NOT have inline property
        Assert.False(root.TryGetProperty("inline", out _));
    }

    [Fact]
    public async Task PublicarComentario_ConErrorDeApi_Lanza()
    {
        // Arrange: simulamos 3 errores 500 seguidos (se agotan los reintentos).
        var handler = new FakeHttpMessageHandler(
            CreateJsonResponse(new { }, HttpStatusCode.InternalServerError),
            CreateJsonResponse(new { }, HttpStatusCode.InternalServerError),
            CreateJsonResponse(new { }, HttpStatusCode.InternalServerError)
        );

        var cliente = CrearCliente(handler);
        cliente.EsperarEntreReintentos = (_, _) => Task.CompletedTask;
        var hallazgo = new Hallazgo("src/Bar.cs", 10, "error", "Fail", "detail");

        // Act: antes se tragaba el fallo. Ahora se propaga, porque el ejecutor anota
        // el comentario como publicado en cuanto esta llamada vuelve: dar por buena una
        // publicación que no ocurrió perdería el hallazgo para siempre.
        await Assert.ThrowsAsync<HttpRequestException>(
            () => cliente.PublicarComentario("workspace/repo", 789, hallazgo));

        // Assert: el cliente agotó el tope de reintentos antes de rendirse.
        Assert.Equal(3, handler.Requests.Count);
    }
}