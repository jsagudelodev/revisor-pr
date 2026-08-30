using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RevisorPrs.Servicio;
using Xunit;

namespace RevisorPrs.Tests;

public class EventoPrMapperTests
{
    private readonly EventoPrMapper _mapper;

    public EventoPrMapperTests()
    {
        _mapper = new EventoPrMapper(NullLogger<EventoPrMapper>.Instance);
    }

    [Fact]
    public void Mapear_JsonValido_CreaEventoPrCorrecto()
    {
        // Arrange
        var json = JsonDocument.Parse(
            """
            {
                "type": "pullrequest",
                "id": 123,
                "title": "Add new feature",
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
                        "name": "main"
                    }
                }
            }
            """).RootElement;

        // Act
        var resultado = _mapper.Mapear(json);

        // Assert
        Assert.NotNull(resultado);
        Assert.Equal("workspace/repo", resultado!.Repositorio);
        Assert.Equal(123, resultado.Numero);
        Assert.Equal("a1b2c3d4e5f6789012345678901234567890abcd", resultado.Commit);
        Assert.Equal("Add new feature", resultado.Titulo);
        Assert.Equal("main", resultado.Rama);
    }

    [Fact]
    public void MapearLista_JsonArray_ReturnsMappedEvents()
    {
        // Arrange
        var jsonArray = JsonDocument.Parse(
            """
            [
                {
                    "type": "pullrequest",
                    "id": 1,
                    "title": "First PR",
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
                            "name": "main"
                        }
                    }
                },
                {
                    "type": "pullrequest",
                    "id": 2,
                    "title": "Second PR",
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
                            "name": "develop"
                        }
                    }
                }
            ]
            """).RootElement;

        // Act
        var resultado = _mapper.MapearLista(jsonArray);

        // Assert
        Assert.Equal(2, resultado.Count());
        Assert.Equal("workspace/repo", resultado.ElementAt(0).Repositorio);
        Assert.Equal(1, resultado.ElementAt(0).Numero);
        Assert.Equal("commit1", resultado.ElementAt(0).Commit);
        Assert.Equal("First PR", resultado.ElementAt(0).Titulo);
        Assert.Equal("main", resultado.ElementAt(0).Rama);

        Assert.Equal("workspace/repo", resultado.ElementAt(1).Repositorio);
        Assert.Equal(2, resultado.ElementAt(1).Numero);
        Assert.Equal("commit2", resultado.ElementAt(1).Commit);
        Assert.Equal("Second PR", resultado.ElementAt(1).Titulo);
        Assert.Equal("develop", resultado.ElementAt(1).Rama);
    }

    [Fact]
    public void Mapear_JsonConCamposFaltantes_ReturnsNull()
    {
        // Arrange - Falta el título
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
                        "name": "main"
                    }
                }
            }
            """).RootElement;

        // Act
        var resultado = _mapper.Mapear(json);

        // Assert
        Assert.Null(resultado);
    }

    [Fact]
    public void Mapear_JsonMalformed_ReturnsNullWithoutException()
    {
        // Arrange - JSON con estructura inesperada
        var json = JsonDocument.Parse(
            """
            {
                "unknown": "value"
            }
            """).RootElement;

        // Act
        var resultado = _mapper.Mapear(json);

        // Assert
        Assert.Null(resultado);
    }
}