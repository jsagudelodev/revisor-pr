using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RevisorPrs.Servicio;
using Xunit;

namespace RevisorPrs.Tests;

public class TraductorEventoPrTests
{
    private readonly TraductorEventoPr _mapper;

    public TraductorEventoPrTests()
    {
        _mapper = new TraductorEventoPr(NullLogger<TraductorEventoPr>.Instance);
    }

    [Fact]
    public void Traducir_JsonValido_CreaEventoPrCorrecto()
    {
        // Preparar
        var json = JsonDocument.Parse(
            """
            {
                "type": "pullrequest",
                "id": 123,
                "title": "Agregar nueva característica",
                "links": {
                    "html": {
                        "href": "https://bitbucket.org/workspace/repo/pull-requests/123"
                    }
                },
                "source": {
                    "commit": {
                        "hash": "a1b2c3d4e5f6789012345678901234567890abcd"
                    }
                },
                "destination": {
                    "branch": {
                        "name": "principal"
                    }
                }
            }
            """).RootElement;

        // Actuar
        var resultado = _mapper.Traducir(json);

        // Afirmar
        Assert.NotNull(resultado);
        Assert.Equal("workspace/repo", resultado!.Repositorio);
        Assert.Equal(123, resultado.Numero);
        Assert.Equal("a1b2c3d4e5f6789012345678901234567890abcd", resultado.Commit);
        Assert.Equal("Agregar nueva característica", resultado.Titulo);
        Assert.Equal("principal", resultado.Rama);
    }

    [Fact]
    public void TraducirLista_ConArrayJson_DevuelveLosEventosTraducidos()
    {
        // Preparar
        var jsonArray = JsonDocument.Parse(
            """
            [
                {
                    "type": "pullrequest",
                    "id": 1,
                    "title": "Primer PR",
                    "links": {
                        "html": {
                            "href": "https://bitbucket.org/workspace/repo/pull-requests/1"
                        }
                    },
                    "source": {
                        "commit": {
                            "hash": "commit1"
                        }
                    },
                    "destination": {
                        "branch": {
                            "name": "principal"
                        }
                    }
                },
                {
                    "type": "pullrequest",
                    "id": 2,
                    "title": "Segundo PR",
                    "links": {
                        "html": {
                            "href": "https://bitbucket.org/workspace/repo/pull-requests/2"
                        }
                    },
                    "source": {
                        "commit": {
                            "hash": "commit2"
                        }
                    },
                    "destination": {
                        "branch": {
                            "name": "desarrollo"
                        }
                    }
                }
            ]
            """).RootElement;

        // Actuar
        var resultado = _mapper.TraducirLista(jsonArray);

        // Afirmar
        Assert.Equal(2, resultado.Count());
        Assert.Equal("workspace/repo", resultado.ElementAt(0).Repositorio);
        Assert.Equal(1, resultado.ElementAt(0).Numero);
        Assert.Equal("commit1", resultado.ElementAt(0).Commit);
        Assert.Equal("Primer PR", resultado.ElementAt(0).Titulo);
        Assert.Equal("principal", resultado.ElementAt(0).Rama);

        Assert.Equal("workspace/repo", resultado.ElementAt(1).Repositorio);
        Assert.Equal(2, resultado.ElementAt(1).Numero);
        Assert.Equal("commit2", resultado.ElementAt(1).Commit);
        Assert.Equal("Segundo PR", resultado.ElementAt(1).Titulo);
        Assert.Equal("desarrollo", resultado.ElementAt(1).Rama);
    }

    [Fact]
    public void Traducir_ConCamposFaltantes_DevuelveNulo()
    {
        // Preparar - Falta el título
        var json = JsonDocument.Parse(
            """
            {
                "type": "pullrequest",
                "id": 123,
                "links": {
                    "html": {
                        "href": "https://bitbucket.org/workspace/repo/pull-requests/123"
                    }
                },
                "source": {
                    "commit": {
                        "hash": "a1b2c3d4e5f6789012345678901234567890abcd"
                    }
                },
                "destination": {
                    "branch": {
                        "name": "principal"
                    }
                }
            }
            """).RootElement;

        // Actuar
        var resultado = _mapper.Traducir(json);

        // Afirmar
        Assert.Null(resultado);
    }

    [Fact]
    public void Traducir_ConJsonMalformado_DevuelveNuloSinExcepcion()
    {
        // Preparar - JSON con estructura inesperada
        var json = JsonDocument.Parse(
            """
            {
                "unknown": "valor"
            }
            """).RootElement;

        // Actuar
        var resultado = _mapper.Traducir(json);

        // Afirmar
        Assert.Null(resultado);
    }
}
