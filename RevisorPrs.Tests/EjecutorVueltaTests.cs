using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RevisorPrs.Servicio;
using Xunit;

namespace RevisorPrs.Tests;

public class EjecutorVueltaTests
{
    private readonly ILogger<EjecutorVuelta> _logger = new RegistradorFalso<EjecutorVuelta>();
    private readonly ClienteBitbucketFalso _clienteBitbucket = new();
    private readonly DecisorRevisar _decisor = new(new RegistradorFalso<DecisorRevisar>());
    private readonly RevisorFalso _revisor = new();
    private readonly AlmacenFalso _almacen = new();
    private readonly ConfiguracionSondeo _configuracionSondeo = new() { Repositorios = new[] { "test/repo" } };

    private EjecutorVuelta CrearSut() => new(
        _logger, 
        _clienteBitbucket, 
        _decisor, 
        _revisor, 
        _almacen, 
        _configuracionSondeo
    );

    [Fact]
    public async Task EjecutarAsync_RecorridoCompleto_LlamaATodasLasPiezas()
    {
        // Arrange
        var sut = CrearSut();
        var pr = new EventoPr("test/repo", 1, "abc", "titulo", "rama");
        _clienteBitbucket.Prs["test/repo"] = new List<EventoPr> { pr };
        
        // Simular que el decisor (tras la primera vuelta en vacío) decide revisarlo
        _decisor.FiltrarPrsParaRevisar(new PullRequest[0]); // primera vuelta
        
        // Act
        await sut.EjecutarAsync(CancellationToken.None);

        // Assert
        Assert.Single(_clienteBitbucket.LlamadasObtenerDiff);
        Assert.Equal("test/repo", _clienteBitbucket.LlamadasObtenerDiff.First().repositorio);
        Assert.Equal(1, _clienteBitbucket.LlamadasObtenerDiff.First().numero);

        Assert.True(_revisor.Llamado);

        Assert.Single(_clienteBitbucket.LlamadasPublicarComentario);

        Assert.Contains(_almacen.Revisados, r => r.idPr == "1" && r.hashCommit == "abc");
    }
    
    [Fact]
    public async Task EjecutarAsync_CuandoUnPrFalla_ContinuaConElSiguiente()
    {
        // Arrange
        var sut = CrearSut();
        var pr1 = new EventoPr("test/repo", 1, "abc", "titulo1", "rama1");
        var pr2 = new EventoPr("test/repo", 2, "def", "titulo2", "rama2");
        _clienteBitbucket.Prs["test/repo"] = new List<EventoPr> { pr1, pr2 };
        _clienteBitbucket.FallaEnPr = 1; // Simular fallo al obtener diff del PR 1

        _decisor.FiltrarPrsParaRevisar(new PullRequest[0]); // primera vuelta

        // Act
        await sut.EjecutarAsync(CancellationToken.None);

        // Assert
        Assert.Collection(_clienteBitbucket.LlamadasObtenerDiff.OrderBy(c => c.numero),
            c => Assert.Equal(1, c.numero),
            c => Assert.Equal(2, c.numero)
        );
        
        Assert.True(_revisor.Llamado); // Se llamó para el PR 2
        Assert.Single(_clienteBitbucket.LlamadasPublicarComentario);
        Assert.Equal(2, _clienteBitbucket.LlamadasPublicarComentario.First().numero);
        
        Assert.DoesNotContain(_almacen.Revisados, r => r.idPr == "1");
        Assert.Contains(_almacen.Revisados, r => r.idPr == "2" && r.hashCommit == "def");
    }

    private class ClienteBitbucketFalso : IClienteBitbucket
    {
        public Dictionary<string, List<EventoPr>> Prs { get; } = new();
        public int FallaEnPr { get; set; } = -1;
        public List<(string repositorio, int numero)> LlamadasObtenerDiff { get; } = new();
        public List<(string repositorio, int numero, Hallazgo hallazgo)> LlamadasPublicarComentario { get; } = new();

        public Task<IEnumerable<EventoPr>> ListarPrsAbiertos(string repositorio)
        {
            if (Prs.TryGetValue(repositorio, out var prs))
            {
                return Task.FromResult(prs.AsEnumerable());
            }
            return Task.FromResult(Enumerable.Empty<EventoPr>());
        }

        public Task<string> ObtenerDiff(string repositorio, int numero)
        {
            LlamadasObtenerDiff.Add((repositorio, numero));
            if (numero == FallaEnPr)
            {
                throw new System.Exception("Fallo simulado");
            }
            return Task.FromResult("diff");
        }

        public Task PublicarComentario(string repositorio, int numero, Hallazgo hallazgo)
        {
            LlamadasPublicarComentario.Add((repositorio, numero, hallazgo));
            return Task.CompletedTask;
        }
    }

    private class RevisorFalso : IRevisor
    {
        public bool Llamado { get; private set; }
        public Task<ResultadoRevision> RevisarAsync(string diff, CancellationToken token = default)
        {
            Llamado = true;
            return Task.FromResult(ResultadoRevision.Ok(new List<Hallazgo>
            {
                new Hallazgo("archivo.cs", 1, "info", "resumen", "detalle")
            }));
        }
    }

    private class AlmacenFalso : IAlmacen
    {
        public List<(string idPr, string hashCommit)> Revisados { get; } = new();
        
        public Task AplicarMigraciones(CancellationToken cancelacion) => Task.CompletedTask;

        public Task MarcarComoRevisado(string idPr, string hashCommit, CancellationToken cancelacion)
        {
            Revisados.Add((idPr, hashCommit));
            return Task.CompletedTask;
        }

        public Task<string?> ObtenerUltimoCommitRevisado(string idPr, CancellationToken cancelacion) => Task.FromResult<string?>(null);
        public Task RegistrarComentario(string idPr, string hashCommit, string idComentario, CancellationToken cancelacion) => Task.CompletedTask;
        public Task<string?> ObtenerIdComentario(string idPr, string hashCommit, CancellationToken cancelacion) => Task.FromResult<string?>(null);

        public void MarcarRevisado(string slugRepo, int idPr, string hashCommit)
        {
            Revisados.Add((idPr.ToString(), hashCommit));
        }

        public bool Revisado(string slugRepo, int idPr, string hashCommit) => false;
    }
}