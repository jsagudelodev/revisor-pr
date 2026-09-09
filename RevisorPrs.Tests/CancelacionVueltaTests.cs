using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RevisorPrs.Servicio;
using Xunit;

namespace RevisorPrs.Tests;

/// <summary>
/// Pruebas de que <see cref="EjecutorVuelta"/> propaga su token de cancelación a las
/// llamadas de Bitbucket y abandona la vuelta cuando el servicio se para.
///
/// Complementan a <see cref="CancelacionClienteTests"/>: allí se comprueba que el
/// cliente respeta el token; aquí, que el ejecutor se lo pasa de verdad en lugar de
/// dejar que cada llamada use uno vacío.
/// </summary>
public class CancelacionVueltaTests
{
    private static EjecutorVuelta CrearSut(IClienteBitbucket cliente, string[] repositorios)
    {
        var decisor = new DecisorRevisar(new RegistradorFalso<DecisorRevisar>());
        decisor.FiltrarPrsParaRevisar(Array.Empty<PullRequest>()); // consume la primera vuelta

        return new EjecutorVuelta(
            new RegistradorFalso<EjecutorVuelta>(),
            cliente,
            decisor,
            new RevisorSinHallazgos(),
            new AlmacenPermisivo(),
            new ConfiguracionSondeo { Repositorios = repositorios },
            ahora: () => DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public async Task EjecutarAsync_PasaSuTokenALasLlamadasDeBitbucket()
    {
        var cliente = new ClienteQueRegistraToken();
        cliente.Prs["repo/uno"] = new List<EventoPr>
        {
            new("repo/uno", 1, "commit-1", "titulo", "rama"),
        };

        var sut = CrearSut(cliente, new[] { "repo/uno" });

        using var cts = new CancellationTokenSource();
        await sut.EjecutarAsync(cts.Token);

        // El token que recibió el cliente debe ser el de la vuelta, no uno vacío:
        // cancelarlo ahora tiene que verse desde el token que se le entregó.
        Assert.NotEmpty(cliente.TokensRecibidos);
        Assert.All(cliente.TokensRecibidos, t => Assert.True(t.CanBeCanceled));

        cts.Cancel();
        Assert.All(cliente.TokensRecibidos, t => Assert.True(t.IsCancellationRequested));
    }

    [Fact]
    public async Task EjecutarAsync_ConCancelacionAlListar_AbandonaLaVueltaSinSeguirConLosDemasRepos()
    {
        // Tres repositorios configurados; el servicio se para durante el primer listado.
        var cliente = new ClienteQueCancelaAlListar();
        var sut = CrearSut(cliente, new[] { "repo/uno", "repo/dos", "repo/tres" });

        await sut.EjecutarAsync(cliente.Fuente.Token);

        // No debe recorrer los otros dos repositorios registrando un error por cada uno.
        Assert.Equal(new[] { "repo/uno" }, cliente.Listados.ToArray());
        Assert.Empty(cliente.LlamadasObtenerDiff);
    }

    private sealed class ClienteQueRegistraToken : IClienteBitbucket
    {
        public Dictionary<string, List<EventoPr>> Prs { get; } = new();
        public List<CancellationToken> TokensRecibidos { get; } = new();

        public Task<IEnumerable<EventoPr>> ListarPrsAbiertos(string repositorio, CancellationToken cancelacion = default)
        {
            TokensRecibidos.Add(cancelacion);
            return Task.FromResult(Prs.TryGetValue(repositorio, out var prs)
                ? prs.AsEnumerable()
                : Enumerable.Empty<EventoPr>());
        }

        public Task<string> ObtenerDiff(string repositorio, int numero, CancellationToken cancelacion = default)
        {
            TokensRecibidos.Add(cancelacion);
            return Task.FromResult("diff --git a/A.cs b/A.cs\n@@ -1,1 +1,1 @@\n+x\n");
        }

        public Task PublicarComentario(string repositorio, int numero, Hallazgo hallazgo, CancellationToken cancelacion = default)
        {
            TokensRecibidos.Add(cancelacion);
            return Task.CompletedTask;
        }
    
        /// <summary>Comentarios de resumen publicados (A3).</summary>
        public List<string> Resumenes { get; } = new();

        public Task PublicarComentarioGeneral(string repositorio, int numero, string texto, CancellationToken cancelacion = default)
        {
            Resumenes.Add(texto);
            return Task.CompletedTask;
        }
}

    private sealed class ClienteQueCancelaAlListar : IClienteBitbucket
    {
        public CancellationTokenSource Fuente { get; } = new();
        public List<string> Listados { get; } = new();
        public List<(string repositorio, int numero)> LlamadasObtenerDiff { get; } = new();

        public Task<IEnumerable<EventoPr>> ListarPrsAbiertos(string repositorio, CancellationToken cancelacion = default)
        {
            Listados.Add(repositorio);
            // El servicio se para justo mientras se atiende este repositorio.
            Fuente.Cancel();
            cancelacion.ThrowIfCancellationRequested();
            return Task.FromResult(Enumerable.Empty<EventoPr>());
        }

        public Task<string> ObtenerDiff(string repositorio, int numero, CancellationToken cancelacion = default)
        {
            LlamadasObtenerDiff.Add((repositorio, numero));
            return Task.FromResult(string.Empty);
        }

        public Task PublicarComentario(string repositorio, int numero, Hallazgo hallazgo, CancellationToken cancelacion = default)
            => Task.CompletedTask;
    
        /// <summary>Comentarios de resumen publicados (A3).</summary>
        public List<string> Resumenes { get; } = new();

        public Task PublicarComentarioGeneral(string repositorio, int numero, string texto, CancellationToken cancelacion = default)
        {
            Resumenes.Add(texto);
            return Task.CompletedTask;
        }
}

    private sealed class RevisorSinHallazgos : IRevisor
    {
        public Task<ResultadoRevision> RevisarAsync(string diff, ContextoRevision? contexto = null, CancellationToken token = default)
            => Task.FromResult(ResultadoRevision.Ok(Array.Empty<Hallazgo>()));
    }

    private sealed class AlmacenPermisivo : IAlmacen
    {
        private readonly HashSet<(string, int, string)> _revisados = new();

        public void MarcarRevisado(string slugRepo, int idPr, string hashCommit)
            => _revisados.Add((slugRepo, idPr, hashCommit));

        public bool Revisado(string slugRepo, int idPr, string hashCommit)
            => _revisados.Contains((slugRepo, idPr, hashCommit));

        public IEnumerable<(string Repositorio, int Numero, string Commit)> ListarRevisiones()
            => _revisados.Select(t => (t.Item1, t.Item2, t.Item3)).ToList();

        public void MarcarFallido(string slugRepo, int idPr, string hashCommit, string motivo) { }

        public bool DebeReintentar(string slugRepo, int idPr, DateTimeOffset ahora) => true;

        public IEnumerable<(string Repositorio, int PullRequest, string Commit, string Motivo)> ListarFallos()
            => Array.Empty<(string, int, string, string)>();
    
        // --- Idempotencia por comentario (A2) ---
        private readonly HashSet<(string, int, string)> _comentados = new();

        public bool ComentarioPublicado(string slugRepo, int idPr, string huella)
            => _comentados.Contains((slugRepo, idPr, huella));

        public void MarcarComentarioPublicado(string slugRepo, int idPr, string hashCommit, string huella, string comentario)
            => _comentados.Add((slugRepo, idPr, huella));
}
}
