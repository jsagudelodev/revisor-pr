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

public class ClienteBitbucketTests
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
    public async Task ListarPrsAbiertos_RetornaTodosLosPRsDeTodasLasPaginas()
    {
        // Arrange: primera página con 'next' y un PR, segunda página sin 'next' y otro PR
        var page1 = new
        {
            values = new[]
            {
                new
                {
                    links = new
                    {
                        html = new
                        {
                            href = "https://bitbucket.org/workspace/repo/pull-requests/1"
                        }
                    },
                    id = 1,
                    source = new
                    {
                        commit = new
                        {
                            hash = "abc123"
                        }
                    },
                    title = "PR 1",
                    destination = new
                    {
                        branch = new
                        {
                            name = "main"
                        }
                    }
                }
            },
            next = "https://api.bitbucket.org/2.0/repositories/workspace/repo/pullrequests?state=OPEN&page=2"
        };

        var page2 = new
        {
            values = new[]
            {
                new
                {
                    links = new
                    {
                        html = new
                        {
                            href = "https://bitbucket.org/workspace/repo/pull-requests/2"
                        }
                    },
                    id = 2,
                    source = new
                    {
                        commit = new
                        {
                            hash = "def456"
                        }
                    },
                    title = "PR 2",
                    destination = new
                    {
                        branch = new
                        {
                            name = "dev"
                        }
                    }
                }
            }
            // no next field
        };

        var handler = new FakeHttpMessageHandler(
            CreateJsonResponse(page1),
            CreateJsonResponse(page2)
        );

        var cliente = CrearCliente(handler);

        // Act
        var prs = await cliente.ListarPrsAbiertos("workspace/repo");

        // Assert
        Assert.Equal(2, prs.Count());
        var prList = prs.ToList();
        Assert.Equal("workspace/repo", prList[0].Repositorio);
        Assert.Equal(1, prList[0].Numero);
        Assert.Equal("abc123", prList[0].Commit);
        Assert.Equal("PR 1", prList[0].Titulo);
        Assert.Equal("main", prList[0].Rama);

        Assert.Equal("workspace/repo", prList[1].Repositorio);
        Assert.Equal(2, prList[1].Numero);
        Assert.Equal("def456", prList[1].Commit);
        Assert.Equal("PR 2", prList[1].Titulo);
        Assert.Equal("dev", prList[1].Rama);
    }

    private static HttpResponseMessage CreateJsonResponse(object obj)
    {
        var json = JsonSerializer.Serialize(obj, _jsonOptions);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}