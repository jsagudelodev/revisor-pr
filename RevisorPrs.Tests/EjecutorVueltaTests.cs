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

    private EjecutorVuelta CrearSut(Func<DateTimeOffset>? ahora = null) => new(
        _logger,
        _clienteBitbucket,
        _decisor,
        _revisor,
        _almacen,
        _configuracionSondeo,
        ahora ?? (() => _almacen.AhoraFalso)
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

        Assert.Contains(_almacen.Revisados, r => r.idPr == "1" && r.hashCommit == "abc" && r.slugRepo == "test/repo");
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

    [Fact]
    public async Task EjecutarAsync_DosVueltasSobreMismoEstado_NoPublicaSegundaVez()
    {
        // Arrange
        var sut = CrearSut();
        var pr = new EventoPr("test/repo", 1, "abc", "titulo", "rama");
        _clienteBitbucket.Prs["test/repo"] = new List<EventoPr> { pr };

        // Consumir la primera vuelta del decisor para que la siguiente vez filtre el PR
        _decisor.FiltrarPrsParaRevisar(new PullRequest[0]);

        // Act
        await sut.EjecutarAsync(CancellationToken.None);
        var publicacionesTrasPrimera = _clienteBitbucket.LlamadasPublicarComentario.Count;

        await sut.EjecutarAsync(CancellationToken.None);
        var publicacionesTrasSegunda = _clienteBitbucket.LlamadasPublicarComentario.Count;

        // Assert: la cuenta de publicaciones NO sube en la segunda vuelta
        Assert.Equal(1, publicacionesTrasPrimera);
        Assert.Equal(publicacionesTrasPrimera, publicacionesTrasSegunda);
    }

    [Fact]
    public async Task EjecutarAsync_CommitNuevoSobreMismoPr_SiSeRevisa()
    {
        // Arrange
        var sut = CrearSut();
        var prV1 = new EventoPr("test/repo", 1, "abc", "titulo", "rama");
        var prV2 = new EventoPr("test/repo", 1, "def", "titulo", "rama");
        _clienteBitbucket.Prs["test/repo"] = new List<EventoPr> { prV1 };

        _decisor.FiltrarPrsParaRevisar(new PullRequest[0]);

        // Act: primera vuelta con commit "abc"
        await sut.EjecutarAsync(CancellationToken.None);
        var publicacionesTrasPrimera = _clienteBitbucket.LlamadasPublicarComentario.Count;

        // El PR ahora aparece con un commit nuevo ("def")
        _clienteBitbucket.Prs["test/repo"] = new List<EventoPr> { prV2 };
        await sut.EjecutarAsync(CancellationToken.None);
        var publicacionesTrasSegunda = _clienteBitbucket.LlamadasPublicarComentario.Count;

        // Assert: la cuenta SÍ sube porque el commit es nuevo
        Assert.Equal(1, publicacionesTrasPrimera);
        Assert.Equal(2, publicacionesTrasSegunda);
    }

    [Fact]
    public async Task EjecutarAsync_CuandoUnPrRevienta_ElSiguienteSeProcesaYElFalloQuedaRegistrado()
    {
        // Arrange
        var sut = CrearSut();
        var pr1 = new EventoPr("test/repo", 1, "abc", "titulo1", "rama1");
        var pr2 = new EventoPr("test/repo", 2, "def", "titulo2", "rama2");
        _clienteBitbucket.Prs["test/repo"] = new List<EventoPr> { pr1, pr2 };
        _clienteBitbucket.FallaEnPr = 1;
        _decisor.FiltrarPrsParaRevisar(new PullRequest[0]);

        // Act
        await sut.EjecutarAsync(CancellationToken.None);

        // Assert: el primer PR se intentó, el segundo se completó y publicó
        Assert.Equal(2, _clienteBitbucket.LlamadasObtenerDiff.Count);
        Assert.Single(_clienteBitbucket.LlamadasPublicarComentario);
        Assert.Equal(2, _clienteBitbucket.LlamadasPublicarComentario.First().numero);

        // El motivo del fallo quedó registrado en el almacén
        var fallos = _almacen.ListarFallos().ToList();
        var falloPr1 = Assert.Single(fallos);
        Assert.Equal("test/repo", falloPr1.Repositorio);
        Assert.Equal(1, falloPr1.PullRequest);
        Assert.Equal("abc", falloPr1.Commit);
        Assert.Equal("Fallo simulado", falloPr1.Motivo);
    }

    [Fact]
    public async Task EjecutarAsync_PrQueFallaRepetidamente_NoSeReintentaEnVueltasConsecutivas()
    {
        // Arrange
        var sut = CrearSut();
        var pr = new EventoPr("test/repo", 1, "abc", "titulo", "rama");
        _clienteBitbucket.Prs["test/repo"] = new List<EventoPr> { pr };
        _clienteBitbucket.FallaEnPr = 1;
        _decisor.FiltrarPrsParaRevisar(new PullRequest[0]);

        // Act: tres vueltas seguidas, sin avanzar el reloj
        await sut.EjecutarAsync(CancellationToken.None);
        await sut.EjecutarAsync(CancellationToken.None);
        await sut.EjecutarAsync(CancellationToken.None);

        // Assert: solo la primera vuelta intentó obtener el diff
        Assert.Single(_clienteBitbucket.LlamadasObtenerDiff);
    }

    [Fact]
    public async Task EjecutarAsync_DespuesDelBackoff_VuelveAReintentarElPrFallido()
    {
        // Arrange
        var sut = CrearSut();
        var pr = new EventoPr("test/repo", 1, "abc", "titulo", "rama");
        _clienteBitbucket.Prs["test/repo"] = new List<EventoPr> { pr };
        _clienteBitbucket.FallaEnPr = 1;
        _decisor.FiltrarPrsParaRevisar(new PullRequest[0]);

        // Act 1: primera vuelta falla y deja al PR en backoff
        await sut.EjecutarAsync(CancellationToken.None);
        Assert.Single(_clienteBitbucket.LlamadasObtenerDiff);

        // Act 2: avanzamos el reloj más allá del backoff y volvemos a ejecutar
        _almacen.AhoraFalso = _almacen.AhoraFalso.AddMinutes(5);
        await sut.EjecutarAsync(CancellationToken.None);

        // Assert: el reintento ocurrió
        Assert.Equal(2, _clienteBitbucket.LlamadasObtenerDiff.Count);
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
        public List<(string slugRepo, string idPr, string hashCommit)> Revisados { get; } = new();
        private readonly HashSet<(string, int, string)> _revisados = new();

        public Task AplicarMigraciones(CancellationToken cancelacion) => Task.CompletedTask;

        public Task MarcarComoRevisado(string idPr, string hashCommit, CancellationToken cancelacion)
        {
            return Task.CompletedTask;
        }

        public Task<string?> ObtenerUltimoCommitRevisado(string idPr, CancellationToken cancelacion) => Task.FromResult<string?>(null);
        public Task RegistrarComentario(string idPr, string hashCommit, string idComentario, CancellationToken cancelacion) => Task.CompletedTask;
        public Task<string?> ObtenerIdComentario(string idPr, string hashCommit, CancellationToken cancelacion) => Task.FromResult<string?>(null);

        public IEnumerable<(string Repositorio, int Numero, string Commit)> ListarRevisiones()
        {
            return _revisados
                .Select(t => (t.Item1, t.Item2, t.Item3))
                .ToList();
        }

        public void MarcarRevisado(string slugRepo, int idPr, string hashCommit)
        {
            _revisados.Add((slugRepo, idPr, hashCommit));
            Revisados.Add((slugRepo, idPr.ToString(), hashCommit));
        }

        public bool Revisado(string slugRepo, int idPr, string hashCommit)
        {
            return _revisados.Contains((slugRepo, idPr, hashCommit));
        }

        public List<(string Repositorio, int PullRequest, string Commit, string Motivo)> Fallos { get; } = new();

        public DateTimeOffset AhoraFalso { get; set; } = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public Dictionary<(string, int), DateTimeOffset> ProximosReintentos { get; } = new();

        public void MarcarFallido(string slugRepo, int idPr, string hashCommit, string motivo)
        {
            Fallos.RemoveAll(f => f.Repositorio == slugRepo && f.PullRequest == idPr);
            Fallos.Add((slugRepo, idPr, hashCommit, motivo));
            // Backoff fijo de 1 minuto por vuelta falsa; los tests pueden mover
            // el reloj con AhoraFalso para verificar cuándo se reintenta.
            ProximosReintentos[(slugRepo, idPr)] = AhoraFalso.AddMinutes(1);
        }

        public bool DebeReintentar(string slugRepo, int idPr, DateTimeOffset ahora)
        {
            if (!ProximosReintentos.TryGetValue((slugRepo, idPr), out var proximo))
            {
                return true;
            }
            return ahora >= proximo;
        }

        public IEnumerable<(string Repositorio, int PullRequest, string Commit, string Motivo)> ListarFallos()
        {
            return Fallos.ToList();
        }
    }
}