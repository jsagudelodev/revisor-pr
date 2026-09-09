using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RevisorPrs.Servicio;
using Xunit;

namespace RevisorPrs.Tests;

/// <summary>
/// Pruebas de los arreglos de higiene que quedaban sueltos: backoff que no se limpiaba,
/// próxima vuelta que nunca se publicaba, contenedor duplicado del log y saneador que
/// enmascaraba el nombre de usuario como si fuera una credencial.
/// </summary>
public class PendientesTests
{
    // ---------- Backoff que sobrevivía al éxito ----------

    [Fact]
    public void MarcarRevisado_BorraElBackoffDelPr()
    {
        using var carpeta = new CarpetaTemporal();
        using var almacen = new Almacen(carpeta.Ruta("backoff.db"));

        almacen.MarcarFallido("equipo-a/repo-1", 7, "commit-malo", "el modelo no respondió");
        Assert.Single(almacen.ListarFallos());

        // Un commit posterior sale adelante: el problema está resuelto.
        almacen.MarcarRevisado("equipo-a/repo-1", 7, "commit-bueno");

        Assert.Empty(almacen.ListarFallos());
        Assert.True(almacen.DebeReintentar("equipo-a/repo-1", 7, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void MarcarRevisado_TrasUnExito_ElBackoffSiguienteEmpiezaDeCero()
    {
        using var carpeta = new CarpetaTemporal();
        using var almacen = new Almacen(carpeta.Ruta("backoff-reinicio.db"));

        // Cuatro fallos seguidos llevarían el backoff a 8 minutos.
        for (int i = 0; i < 4; i++)
        {
            almacen.MarcarFallido("equipo-a/repo-1", 7, "commit-malo", "fallo");
        }

        almacen.MarcarRevisado("equipo-a/repo-1", 7, "commit-bueno");
        almacen.MarcarFallido("equipo-a/repo-1", 7, "commit-nuevo", "fallo aislado");

        // Si el contador no se hubiera reiniciado, el próximo reintento caería a 16
        // minutos en vez de a 1: un fallo puntual heredaría el castigo de hace semanas.
        var ahora = DateTimeOffset.UtcNow;
        Assert.False(almacen.DebeReintentar("equipo-a/repo-1", 7, ahora));
        Assert.True(almacen.DebeReintentar("equipo-a/repo-1", 7, ahora.AddMinutes(2)));
    }

    [Fact]
    public void MarcarRevisado_NoTocaElBackoffDeOtrosPrs()
    {
        using var carpeta = new CarpetaTemporal();
        using var almacen = new Almacen(carpeta.Ruta("backoff-aislado.db"));

        almacen.MarcarFallido("equipo-a/repo-1", 7, "c1", "fallo");
        almacen.MarcarFallido("equipo-a/repo-1", 8, "c2", "fallo");

        almacen.MarcarRevisado("equipo-a/repo-1", 7, "c1");

        var fallo = Assert.Single(almacen.ListarFallos());
        Assert.Equal(8, fallo.PullRequest);
    }

    // ---------- Próxima vuelta que nunca se publicaba ----------

    [Fact]
    public async Task Worker_TrasUnaVuelta_AnunciaLaProximaAlEstado()
    {
        var instante = new DateTimeOffset(2025, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var estado = new EstadoServicio(() => instante);
        var reloj = new RelojQueParaTrasNEsperas(1);

        var worker = new WorkerAccesible(
            new RegistradorFalso<Worker>(),
            new ConfiguracionSondeo { IntervaloMinutos = 5, Repositorios = new[] { "a/b" } },
            new EjecutorQueNoHaceNada(),
            reloj,
            estado);

        await worker.Arrancar(reloj.Fuente.Token);

        // Antes quedaba siempre en null: nadie llamaba a AnunciarProximoSondeo.
        var foto = estado.Capturar();
        Assert.Equal(instante.AddMinutes(5), foto.ProximaVueltaUtc);
    }

    [Fact]
    public void EstadoServicio_SinAnuncio_DejaLaProximaVueltaEnNulo()
    {
        // El valor solo se publica cuando el sondeo lo anuncia: sin vueltas, no hay dato.
        Assert.Null(new EstadoServicio().Capturar().ProximaVueltaUtc);
    }

    // ---------- Log sin contenedor duplicado ----------

    [Fact]
    public void Composicion_RegistraElLogRotativoComoUnicoProveedor()
    {
        using var carpeta = new CarpetaTemporal();

        IConfiguration configuracion = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sondeo:IntervaloMinutos"] = "5",
                ["Sondeo:Repositorios:0"] = "equipo-a/repo-1",
                ["Bitbucket:Usuario"] = "usuario",
                ["Bitbucket:ClaveAplicacion"] = "clave",
                ["Llm:Endpoint"] = "https://api.ejemplo.invalid/v1/chat/completions",
                ["Llm:Modelo"] = "modelo",
                ["Estado:Puerto"] = "0",
                ["Registro:RutaFichero"] = carpeta.Ruta("revisor.log"),
                ["BaseDatos:RutaBaseDatos"] = carpeta.Ruta("log.db"),
            })
            .Build();

        var servicios = new ServiceCollection();
        servicios.AddLogging();
        ComposicionServicios.Registrar(servicios, configuracion);
        using var proveedor = servicios.BuildServiceProvider();

        // El proveedor rotativo llega al sistema de log por DI, sin construir un
        // contenedor aparte del que salían singletons duplicados y sin liberar.
        var rotativo = proveedor.GetRequiredService<ProveedorRegistrosRotativo>();
        var comoProveedorDeLog = proveedor.GetServices<ILoggerProvider>()
            .OfType<ProveedorRegistrosRotativo>()
            .ToList();

        var registrado = Assert.Single(comoProveedorDeLog);
        Assert.Same(rotativo, registrado);
    }

    // ---------- Saneador que mutilaba el log ----------

    [Fact]
    public void DesdeConfiguracion_NoEnmascaraElNombreDeUsuario()
    {
        // El saneador sustituye SUBCADENAS, así que tratar el usuario como secreto
        // mutilaba el log: con el usuario "ada", la línea que el ejecutor emite al
        // cerrar cada vuelta salía como "Vuelta de sondeo finaliz***.".
        var saneador = SaneadorSecretos.DesdeConfiguracion(Configuracion(
            usuario: "ada",
            clave: "clave-de-aplicacion-larga",
            claveApi: "sk-clave-llm-larga"));

        const string mensaje = "Vuelta de sondeo finalizada.";
        Assert.Equal(mensaje, saneador.Sanear(mensaje));
        Assert.False(saneador.ContieneSecreto(mensaje));
    }

    [Fact]
    public void DesdeConfiguracion_SigueEnmascarandoLasCredenciales()
    {
        var saneador = SaneadorSecretos.DesdeConfiguracion(Configuracion(
            usuario: "dev",
            clave: "clave-de-aplicacion-larga",
            claveApi: "sk-clave-llm-larga"));

        Assert.DoesNotContain(
            "clave-de-aplicacion-larga",
            saneador.Sanear("Authorization: clave-de-aplicacion-larga"),
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "sk-clave-llm-larga",
            saneador.Sanear("api key sk-clave-llm-larga"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void DesdeConfiguracion_EnmascaraLaCredencialBasicCodificada()
    {
        // La cabecera Authorization no lleva la clave en claro, sino base64("usuario:clave").
        var saneador = SaneadorSecretos.DesdeConfiguracion(Configuracion(
            usuario: "dev",
            clave: "clave-de-aplicacion-larga",
            claveApi: "sk-clave-llm-larga"));

        string basic = Convert.ToBase64String(Encoding.ASCII.GetBytes("dev:clave-de-aplicacion-larga"));

        Assert.True(saneador.ContieneSecreto("Authorization: Basic " + basic));
        Assert.DoesNotContain(basic, saneador.Sanear("Authorization: Basic " + basic), StringComparison.Ordinal);
    }

    private static IConfiguration Configuracion(string usuario, string clave, string claveApi)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bitbucket:Usuario"] = usuario,
                ["Bitbucket:ClaveAplicacion"] = clave,
                ["Llm:ClaveApi"] = claveApi,
            })
            .Build();

    // ---------- Utilidades ----------

    /// <summary>Expone el arranque, que en BackgroundService es protegido.</summary>
    private sealed class WorkerAccesible : Worker
    {
        public WorkerAccesible(
            ILogger<Worker> logger,
            ConfiguracionSondeo configuracion,
            IEjecutorVuelta ejecutor,
            IReloj reloj,
            EstadoServicio estado)
            : base(logger, configuracion, ejecutor, reloj, estado)
        {
        }

        public Task Arrancar(CancellationToken cancelacion) => StartAsync(cancelacion);
    }

    /// <summary>Cancela el bucle del sondeo tras N esperas, para que el test termine.</summary>
    private sealed class RelojQueParaTrasNEsperas : IReloj
    {
        private readonly int _esperasMaximas;
        private int _esperas;

        public RelojQueParaTrasNEsperas(int esperasMaximas) => _esperasMaximas = esperasMaximas;

        public CancellationTokenSource Fuente { get; } = new();

        public Task EsperarAsync(TimeSpan intervalo, CancellationToken cancelacion)
        {
            if (++_esperas >= _esperasMaximas)
            {
                Fuente.Cancel();
            }
            return Task.CompletedTask;
        }
    }

    private sealed class EjecutorQueNoHaceNada : IEjecutorVuelta
    {
        public Task EjecutarAsync(CancellationToken cancelacion) => Task.CompletedTask;
    
    /// <summary>Pull requests revisados por aviso de webhook (C2).</summary>
    public List<PullRequest> RevisadosPorAviso { get; } = new();

    public Task RevisarPrAsync(PullRequest pr, CancellationToken cancelacion)
    {
        RevisadosPorAviso.Add(pr);
        return Task.CompletedTask;
    }
}

    private sealed class CarpetaTemporal : IDisposable
    {
        private readonly string _ruta =
            Path.Combine(Path.GetTempPath(), "revisorprs-pendientes-" + Guid.NewGuid().ToString("N"));

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
