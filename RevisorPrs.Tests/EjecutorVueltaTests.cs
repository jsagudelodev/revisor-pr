using System;
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

    /// <summary>
    /// RV.17: una vuelta se "interrumpe" a mitad (un PR falla al obtener el diff antes de
    /// marcarse como revisado, y otro PR se procesa y se marca). Al lanzar una segunda
    /// instancia del EjecutorVuelta sobre el mismo almacén/base, el PR ya marcado NO se
    /// vuelve a procesar (no se repite su comentario) y el PR que se quedó a medias SÍ
    /// se procesa, publica y se marca.
    /// </summary>
    [Fact]
    public async Task EjecutarAsync_VueltaInterrumpidaALaMitad_OtraInstanciaSobreMismaBase_NoRepiteLoMarcado_YProcesaLoFaltante()
    {
        // Arrange
        // PR 1 falla al obtener el diff -> NO se procesa y NO se marca como revisado.
        // PR 2 se procesa con éxito -> se publica su comentario y se marca como revisado.
        var pr1 = new EventoPr("test/repo", 1, "abc", "titulo1", "rama1");
        var pr2 = new EventoPr("test/repo", 2, "def", "titulo2", "rama2");
        _clienteBitbucket.Prs["test/repo"] = new List<EventoPr> { pr1, pr2 };
        _clienteBitbucket.FallaEnPr = 1;

        // Act 1: primera vuelta (la que se "interrumpe" conceptualmente al fallar PR 1)
        var sut1 = CrearSut();
        await sut1.EjecutarAsync(CancellationToken.None);

        var publicacionesTrasPrimera = _clienteBitbucket.LlamadasPublicarComentario.Count;
        var diffsTrasPrimera = _clienteBitbucket.LlamadasObtenerDiff.Count;
        Assert.Equal(1, publicacionesTrasPrimera); // solo el PR 2 publicó
        Assert.Equal(2, diffsTrasPrimera); // intentó diff de PR 1 (falló) y de PR 2 (ok)
        Assert.DoesNotContain(_almacen.Revisados, r => r.idPr == "1");
        Assert.Contains(_almacen.Revisados, r => r.idPr == "2" && r.hashCommit == "def");

        // El PR 1 ya no falla cuando volvamos a intentar: simulamos "reintento" relajando el fallo.
        _clienteBitbucket.FallaEnPr = -1;

        // Act 2: nueva instancia del EjecutorVuelta sobre el mismo almacén
        var sut2 = CrearSut();
        await sut2.EjecutarAsync(CancellationToken.None);

        var publicacionesTrasSegunda = _clienteBitbucket.LlamadasPublicarComentario.Count;
        var diffsTrasSegunda = _clienteBitbucket.LlamadasObtenerDiff.Count;

        // Assert
        // Publicaciones: en la segunda vuelta solo se añade la del PR 1. Total = 2.
        Assert.Equal(2, publicacionesTrasSegunda);

        // El PR 2 publicó exactamente 1 vez en toda la historia (en la primera vuelta): no se repite.
        Assert.Single(_clienteBitbucket.LlamadasPublicarComentario.Where(p => p.numero == 2));
        // El PR 1 publicó exactamente 1 vez, y fue en la segunda vuelta.
        Assert.Single(_clienteBitbucket.LlamadasPublicarComentario.Where(p => p.numero == 1));

        // Diffs: en la primera vuelta se pidió diff del PR 1 (falló) y del PR 2.
        // En la segunda vuelta solo se pidió el diff del PR 1 (el PR 2 ya estaba marcado).
        // Totales: PR1 (1ª vuelta, falló) + PR2 (1ª vuelta) + PR1 (2ª vuelta) = 3.
        Assert.Equal(3, diffsTrasSegunda);
        Assert.Equal(2, _clienteBitbucket.LlamadasObtenerDiff.Count(c => c.numero == 1));
        Assert.Single(_clienteBitbucket.LlamadasObtenerDiff.Where(c => c.numero == 2));

        // Estado final del almacén: ambos PRs marcados con su commit correspondiente.
        Assert.Contains(_almacen.Revisados, r => r.idPr == "1" && r.hashCommit == "abc" && r.slugRepo == "test/repo");
        Assert.Contains(_almacen.Revisados, r => r.idPr == "2" && r.hashCommit == "def" && r.slugRepo == "test/repo");
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
                throw new Exception("Fallo simulado");
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

        public void MarcarRevisado(string slugRepo, int idPr, string hashCommit)
        {
            _revisados.Add((slugRepo, idPr, hashCommit));
            Revisados.Add((slugRepo, idPr.ToString(), hashCommit));
        }

        public bool Revisado(string slugRepo, int idPr, string hashCommit)
        {
            return _revisados.Contains((slugRepo, idPr, hashCommit));
        }
    }
}