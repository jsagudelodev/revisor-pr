using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RevisorPrs.Servicio;
using Xunit;

namespace RevisorPrs.Tests;

/// <summary>
/// Pruebas de que un comentario no se publica dos veces en el mismo pull request (A2).
///
/// El pull request no se da por revisado hasta que termina el bucle de publicación, así
/// que una caída a mitad hacía que la vuelta siguiente republicara los comentarios que
/// ya estaban en el PR. Con cinco hallazgos y una caída en el tercero, la segunda vuelta
/// publicaba los cinco: dos duplicados a la vista del autor.
/// </summary>
public class IdempotenciaComentariosTests
{
    private static readonly EventoPr Pr = new("equipo-a/repo-1", 1, "commit-1", "titulo", "rama");

    [Fact]
    public async Task CaidaAMitadDeLaPublicacion_AlReanudar_SoloPublicaLosQueFaltaban()
    {
        using var carpeta = new CarpetaTemporal();
        string ruta = carpeta.Ruta("idempotencia.db");

        // Primera instancia: la API falla al publicar el tercero de cinco.
        var primera = new ClienteQueFallaAlPublicar(fallarEnComentario: 3);
        primera.Prs["equipo-a/repo-1"] = new List<EventoPr> { Pr };

        using (var almacen = new Almacen(ruta))
        {
            await CrearEjecutor(primera, almacen).EjecutarAsync(CancellationToken.None);
        }

        Assert.Equal(new[] { "h1", "h2" }, primera.Publicados.ToArray());

        // Segunda instancia sobre la misma base, ya vencido el backoff del fallo.
        var segunda = new ClienteQueFallaAlPublicar(fallarEnComentario: -1);
        segunda.Prs["equipo-a/repo-1"] = new List<EventoPr> { Pr };

        using (var almacen = new Almacen(ruta))
        {
            await CrearEjecutor(segunda, almacen, DateTimeOffset.UtcNow.AddHours(1))
                .EjecutarAsync(CancellationToken.None);
        }

        // Los dos primeros ya estaban en el PR y no se repiten; se retoma en el tercero.
        Assert.Equal(new[] { "h3", "h4", "h5" }, segunda.Publicados.ToArray());
    }

    [Fact]
    public async Task VueltaRepetidaSinCambios_NoRepiteNingunComentario()
    {
        using var carpeta = new CarpetaTemporal();
        string ruta = carpeta.Ruta("repetida.db");

        var cliente = new ClienteQueFallaAlPublicar(fallarEnComentario: -1);
        cliente.Prs["equipo-a/repo-1"] = new List<EventoPr> { Pr };

        using (var almacen = new Almacen(ruta))
        {
            var sut = CrearEjecutor(cliente, almacen);
            await sut.EjecutarAsync(CancellationToken.None);
            await sut.EjecutarAsync(CancellationToken.None);
        }

        Assert.Equal(5, cliente.Publicados.Count);
    }

    [Fact]
    public void Huella_IgnoraLaProsaDelModelo()
    {
        // Al reintentar se vuelve a llamar al LLM sobre el mismo diff y puede reformular.
        // La huella tiene que aguantar eso, o no cubriría el caso para el que existe.
        var original = new Hallazgo("src/A.cs", 12, "error", "Posible desbordamiento", "detalle");
        var reformulado = new Hallazgo("src/A.cs", 12, "error", "Puede desbordarse el índice", "otro detalle");

        Assert.Equal(original.Huella(), reformulado.Huella());
    }

    [Fact]
    public void Huella_DistingueArchivoLineaYSeveridad()
    {
        var baseHallazgo = new Hallazgo("src/A.cs", 12, "error", "resumen", "detalle");

        Assert.NotEqual(baseHallazgo.Huella(), (baseHallazgo with { Archivo = "src/B.cs" }).Huella());
        Assert.NotEqual(baseHallazgo.Huella(), (baseHallazgo with { Linea = 13 }).Huella());
        Assert.NotEqual(baseHallazgo.Huella(), (baseHallazgo with { Severidad = "info" }).Huella());

        // La severidad se compara sin distinguir mayúsculas ni espacios sobrantes.
        Assert.Equal(baseHallazgo.Huella(), (baseHallazgo with { Severidad = " ERROR " }).Huella());
    }

    [Fact]
    public void Almacen_ComentarioPublicado_EsIndependientePorPrYPorRepositorio()
    {
        using var carpeta = new CarpetaTemporal();
        using var almacen = new Almacen(carpeta.Ruta("aislamiento.db"));

        string huella = new Hallazgo("src/A.cs", 1, "error", "r", "d").Huella();
        almacen.MarcarComentarioPublicado("equipo-a/repo-1", 1, "c1", huella, "r");

        Assert.True(almacen.ComentarioPublicado("equipo-a/repo-1", 1, huella));
        Assert.False(almacen.ComentarioPublicado("equipo-a/repo-1", 2, huella));
        Assert.False(almacen.ComentarioPublicado("equipo-a/repo-2", 1, huella));
    }

    [Fact]
    public void Almacen_MarcarDosVecesLaMismaHuella_NoRompe()
    {
        using var carpeta = new CarpetaTemporal();
        using var almacen = new Almacen(carpeta.Ruta("repetido.db"));

        string huella = new Hallazgo("src/A.cs", 1, "error", "r", "d").Huella();

        // El índice único protege incluso si dos vueltas se solapan.
        almacen.MarcarComentarioPublicado("equipo-a/repo-1", 1, "c1", huella, "r");
        almacen.MarcarComentarioPublicado("equipo-a/repo-1", 1, "c2", huella, "r");

        Assert.True(almacen.ComentarioPublicado("equipo-a/repo-1", 1, huella));
    }

    private static EjecutorVuelta CrearEjecutor(
        IClienteBitbucket cliente,
        IAlmacen almacen,
        DateTimeOffset? ahora = null)
        => new(
            new RegistradorFalso<EjecutorVuelta>(),
            cliente,
            new DecisorRevisar(new RegistradorFalso<DecisorRevisar>(), almacen),
            new RevisorCon5Hallazgos(),
            almacen,
            new ConfiguracionSondeo { Repositorios = new[] { "equipo-a/repo-1" } },
            ahora: () => ahora ?? DateTimeOffset.UtcNow);

    private sealed class ClienteQueFallaAlPublicar : IClienteBitbucket
    {
        private readonly int _fallarEn;
        private int _intentos;

        public ClienteQueFallaAlPublicar(int fallarEnComentario) => _fallarEn = fallarEnComentario;

        public Dictionary<string, List<EventoPr>> Prs { get; } = new();
        public List<string> Publicados { get; } = new();

        public Task<IEnumerable<EventoPr>> ListarPrsAbiertos(string repositorio, CancellationToken cancelacion = default)
            => Task.FromResult(Prs.TryGetValue(repositorio, out var p) ? p.AsEnumerable() : Enumerable.Empty<EventoPr>());

        public Task<string> ObtenerDiff(string repositorio, int numero, CancellationToken cancelacion = default)
            => Task.FromResult("diff --git a/A.cs b/A.cs\n@@ -1,9 +1,9 @@\n+cambio\n");

        public Task PublicarComentario(string repositorio, int numero, Hallazgo hallazgo, CancellationToken cancelacion = default)
        {
            if (++_intentos == _fallarEn)
            {
                throw new InvalidOperationException("caída simulada del servicio");
            }
            Publicados.Add(hallazgo.Resumen);
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

    private sealed class RevisorCon5Hallazgos : IRevisor
    {
        public Task<ResultadoRevision> RevisarAsync(string diff, ContextoRevision? contexto = null, CancellationToken token = default)
            => Task.FromResult(ResultadoRevision.Ok(Enumerable.Range(1, 5)
                .Select(i => new Hallazgo("A.cs", i, "error", "h" + i, "detalle"))
                .ToList()));
    }

    private sealed class CarpetaTemporal : IDisposable
    {
        private readonly string _ruta =
            Path.Combine(Path.GetTempPath(), "revisorprs-idem-" + Guid.NewGuid().ToString("N"));

        public CarpetaTemporal() => Directory.CreateDirectory(_ruta);

        public string Ruta(string nombre) => Path.Combine(_ruta, nombre);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_ruta, recursive: true);
            }
            catch (IOException)
            {
                // SQLite puede tardar un instante en soltar el fichero.
            }
        }
    }
}
