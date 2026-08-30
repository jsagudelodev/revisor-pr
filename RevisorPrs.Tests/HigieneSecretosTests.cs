using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RevisorPrs.Servicio;
using Xunit;

namespace RevisorPrs.Tests;

public class HigieneSecretosTests
{
    private const string TestUsuario = "testuser";
    private const string TestClave = "supersecreto123";
    private const string TestToken = "dGVzdHVzZXI6c3VwZXJzZWNyZXQxMjM="; // Base64 of "testuser:supersecreto123"

    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage? _responseToReturn;
        private readonly Queue<HttpResponseMessage> _queue = new();

        public FakeHttpMessageHandler(HttpResponseMessage responseToReturn)
        {
            _responseToReturn = responseToReturn;
        }

        public FakeHttpMessageHandler(params HttpResponseMessage[] responses)
        {
            foreach (var r in responses)
            {
                _queue.Enqueue(r);
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_queue != null && _queue.Count > 0)
            {
                return Task.FromResult(_queue.Dequeue());
            }

            // If we have a single response to return
            if (_responseToReturn != null)
            {
                return Task.FromResult(_responseToReturn);
            }

            var emptyResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"values\":[]}", Encoding.UTF8, "application/json")
            };
            return Task.FromResult(emptyResponse);
        }
    }

    private static ClienteBitbucket CrearClienteConRegistrador(FakeHttpMessageHandler handler, out RegistradorFalso<ClienteBitbucket> logger)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.bitbucket.org/")
        };

        var config = Options.Create(new ConfiguracionBitbucket
        {
            Usuario = TestUsuario,
            ClaveAplicacion = TestClave
        });

        logger = new RegistradorFalso<ClienteBitbucket>();
        var traductor = new TraductorEventoPr(NullLogger<TraductorEventoPr>.Instance);

        return new ClienteBitbucket(httpClient, config, logger, traductor);
    }

    [Fact]
    public async Task ClaveAplicacion_NoApareceEnLog_TrasLlamadaCorrecta()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"values\":[]}", Encoding.UTF8, "application/json")
        };
        var handler = new FakeHttpMessageHandler(response);
        ClienteBitbucket client = CrearClienteConRegistrador(handler, out var logger);

        // Act
        await client.ListarPrsAbiertos("workspace/repo");

        // Assert
        Assert.False(ContieneSecreto(logger.Mensajes, TestClave));
        Assert.False(ContieneSecreto(logger.Mensajes, TestToken));
    }

    [Fact]
    public async Task ClaveAplicacion_NoApareceEnLog_TrasLlamadaFallida()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\": \"unauthorized\"}", Encoding.UTF8, "application/json")
        };
        var handler = new FakeHttpMessageHandler(response);
        ClienteBitbucket client = CrearClienteConRegistrador(handler, out var logger);

        // Act
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.ListarPrsAbiertos("workspace/repo"));

        // Assert
        Assert.False(ContieneSecreto(logger.Mensajes, TestClave));
        Assert.False(ContieneSecreto(logger.Mensajes, TestToken));
    }

    [Fact]
    public async Task CabeceraAutorizacion_NoApareceEnLog_TrasLlamadaFallida()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"error\": \"internal server error\"}", Encoding.UTF8, "application/json")
        };
        var handler = new FakeHttpMessageHandler(response);
        ClienteBitbucket client = CrearClienteConRegistrador(handler, out var logger);

        // Act
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.ListarPrsAbiertos("workspace/repo"));

        // Assert
        Assert.False(ContieneSecreto(logger.Mensajes, TestClave));
        Assert.False(ContieneSecreto(logger.Mensajes, TestToken));
    }

    private static bool ContieneSecreto(IReadOnlyList<string> mensajes, string secreto)
    {
        foreach (var msg in mensajes)
        {
            if (msg.Contains(secreto, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}