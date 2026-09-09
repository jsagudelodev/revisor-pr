using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using RevisorPrs.Servicio;
using Xunit;

namespace RevisorPrs.Tests;

/// <summary>
/// Pruebas del disparo por webhook (C2).
///
/// Con solo sondeo, quien empuja un commit espera hasta el intervalo configurado —cinco
/// minutos por defecto— y para entonces ya cambió de tarea. El aviso arranca la revisión
/// en segundos. El sondeo se mantiene como red de seguridad.
/// </summary>
public class WebhookTests
{
    private const string Secreto = "secreto-compartido-con-bitbucket";

    private static readonly string CuerpoDeEjemplo = """
        {
          "repository": { "full_name": "equipo-a/repo-1" },
          "pullrequest": {
            "id": 42,
            "title": "Arreglar el IVA",
            "description": "Redondeo por línea.",
            "links": { "html": { "href": "https://bitbucket.org/equipo-a/repo-1/pull-requests/42" } },
            "source": { "commit": { "hash": "abc123" } },
            "destination": { "branch": { "name": "main" } }
          }
        }
        """;

    private static ServidorWebhook CrearServidor(ColaDeRevisiones cola, ConfiguracionWebhook? config = null) =>
        new(NullLogger<ServidorWebhook>.Instance,
            config ?? new ConfiguracionWebhook { Habilitado = true, Secreto = Secreto },
            cola,
            new TraductorEventoPr(NullLogger<TraductorEventoPr>.Instance));

    private static ServidorWebhook.PeticionWebhook Peticion(
        string cuerpo,
        IDictionary<string, string>? cabeceras = null)
    {
        byte[] crudo = Encoding.UTF8.GetBytes(cuerpo);
        var mapa = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var par in cabeceras ?? new Dictionary<string, string>())
        {
            mapa[par.Key.ToLowerInvariant()] = par.Value;
        }

        return new ServidorWebhook.PeticionWebhook("POST", "/webhook/bitbucket", mapa, cuerpo, crudo);
    }

    private static string FirmaDe(string cuerpo, string secreto)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secreto));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(cuerpo))).ToLowerInvariant();
    }

    // ---------- Autenticación ----------

    [Fact]
    public void SinCredencial_SeRechaza()
    {
        // Un endpoint que dispara revisiones —y por tanto gasto en el modelo— no puede
        // atender a quien pase por ahí.
        var servidor = CrearServidor(new ColaDeRevisiones());

        Assert.False(servidor.Autenticado(Peticion(CuerpoDeEjemplo)));
    }

    [Fact]
    public void ConFirmaHmacCorrecta_SeAcepta()
    {
        var servidor = CrearServidor(new ColaDeRevisiones());

        var peticion = Peticion(CuerpoDeEjemplo, new Dictionary<string, string>
        {
            ["X-Hub-Signature"] = "sha256=" + FirmaDe(CuerpoDeEjemplo, Secreto),
        });

        Assert.True(servidor.Autenticado(peticion));
    }

    [Fact]
    public void ConFirmaHmacSinPrefijo_TambienSeAcepta()
    {
        var servidor = CrearServidor(new ColaDeRevisiones());

        var peticion = Peticion(CuerpoDeEjemplo, new Dictionary<string, string>
        {
            ["X-Hub-Signature"] = FirmaDe(CuerpoDeEjemplo, Secreto),
        });

        Assert.True(servidor.Autenticado(peticion));
    }

    [Fact]
    public void ConFirmaDeOtroSecreto_SeRechaza()
    {
        var servidor = CrearServidor(new ColaDeRevisiones());

        var peticion = Peticion(CuerpoDeEjemplo, new Dictionary<string, string>
        {
            ["X-Hub-Signature"] = "sha256=" + FirmaDe(CuerpoDeEjemplo, "otro-secreto"),
        });

        Assert.False(servidor.Autenticado(peticion));
    }

    [Fact]
    public void ConCuerpoAlteradoTrasFirmar_SeRechaza()
    {
        // La firma cubre el cuerpo: cambiarlo por el camino invalida el aviso.
        var servidor = CrearServidor(new ColaDeRevisiones());

        var peticion = Peticion(
            CuerpoDeEjemplo.Replace("42", "99", StringComparison.Ordinal),
            new Dictionary<string, string>
            {
                ["X-Hub-Signature"] = "sha256=" + FirmaDe(CuerpoDeEjemplo, Secreto),
            });

        Assert.False(servidor.Autenticado(peticion));
    }

    [Fact]
    public void ConTokenBearerCorrecto_SeAcepta()
    {
        // No todas las instalaciones de Bitbucket ofrecen firma HMAC; con el token en
        // Authorization el webhook sigue siendo usable.
        var servidor = CrearServidor(new ColaDeRevisiones());

        var peticion = Peticion(CuerpoDeEjemplo, new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer " + Secreto,
        });

        Assert.True(servidor.Autenticado(peticion));
    }

    [Fact]
    public void ConTokenBearerIncorrecto_SeRechaza()
    {
        var servidor = CrearServidor(new ColaDeRevisiones());

        var peticion = Peticion(CuerpoDeEjemplo, new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer " + Secreto + "-de-mas",
        });

        Assert.False(servidor.Autenticado(peticion));
    }

    [Fact]
    public void ConCabeceraDeFirmaPersonalizada_SeUsaLaConfigurada()
    {
        var servidor = CrearServidor(new ColaDeRevisiones(), new ConfiguracionWebhook
        {
            Habilitado = true,
            Secreto = Secreto,
            CabeceraFirma = "X-Firma-Propia",
        });

        var peticion = Peticion(CuerpoDeEjemplo, new Dictionary<string, string>
        {
            ["X-Firma-Propia"] = FirmaDe(CuerpoDeEjemplo, Secreto),
        });

        Assert.True(servidor.Autenticado(peticion));
    }

    // ---------- Interpretación del aviso ----------

    [Fact]
    public void DeUnAvisoDePullRequest_SaleElPrConSuContexto()
    {
        var servidor = CrearServidor(new ColaDeRevisiones());

        var pr = servidor.InterpretarEvento(CuerpoDeEjemplo);

        Assert.NotNull(pr);
        Assert.Equal("equipo-a/repo-1", pr!.Repositorio);
        Assert.Equal(42, pr.Numero);
        Assert.Equal("abc123", pr.Commit);
        Assert.Equal("Arreglar el IVA", pr.Titulo);
        Assert.Equal("Redondeo por línea.", pr.Descripcion);
        // La rama de destino hace falta para leer la guía del equipo (B3).
        Assert.Equal("main", pr.RamaDestino);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"repository":{"full_name":"a/b"}}""")]
    [InlineData("no es json")]
    [InlineData("")]
    public void UnAvisoQueNoTraePullRequest_SeIgnoraSinRomper(string cuerpo)
    {
        var servidor = CrearServidor(new ColaDeRevisiones());

        Assert.Null(servidor.InterpretarEvento(cuerpo));
    }

    // ---------- La cola ----------

    [Fact]
    public void LaColaEntregaEnOrdenDeLlegada()
    {
        var cola = new ColaDeRevisiones();
        cola.Encolar(new PullRequest("a/b", 1, "c1"));
        cola.Encolar(new PullRequest("a/b", 2, "c2"));

        Assert.True(cola.IntentarSacar(out var primero));
        Assert.True(cola.IntentarSacar(out var segundo));
        Assert.False(cola.IntentarSacar(out _));

        Assert.Equal(1, primero!.Numero);
        Assert.Equal(2, segundo!.Numero);
    }

    [Fact]
    public void LaColaLlena_DescartaEnVezDeCrecerSinLimite()
    {
        // Lo descartado no se pierde: el sondeo lo recoge en la vuelta siguiente. Es
        // preferible a acumular trabajo sin límite en memoria.
        var cola = new ColaDeRevisiones();

        for (int i = 0; i < ColaDeRevisiones.Capacidad; i++)
        {
            Assert.True(cola.Encolar(new PullRequest("a/b", i, "c")));
        }

        Assert.False(cola.Encolar(new PullRequest("a/b", 9999, "c")));
        Assert.Equal(ColaDeRevisiones.Capacidad, cola.Pendientes);
    }

    // ---------- El Worker consume la cola ----------

    [Fact]
    public async Task ElWorkerRevisaLoQueElWebhookEncola()
    {
        var cola = new ColaDeRevisiones();
        cola.Encolar(new PullRequest("equipo-a/repo-1", 42, "abc123", "titulo", null, "main"));

        var ejecutor = new EjecutorEspia();
        var worker = new Worker(
            new RegistradorFalso<Worker>(),
            new ConfiguracionSondeo { IntervaloMinutos = 5, Repositorios = new[] { "equipo-a/repo-1" } },
            ejecutor,
            new RelojQueNoEspera(),
            estado: null,
            cola: cola);

        await worker.AtenderAvisosAsync(CancellationToken.None);

        var revisado = Assert.Single(ejecutor.PorAviso);
        Assert.Equal(42, revisado.Numero);
        Assert.Equal(0, cola.Pendientes);
    }

    [Fact]
    public async Task UnAvisoQueRevienta_NoTumbaElSondeo()
    {
        var cola = new ColaDeRevisiones();
        cola.Encolar(new PullRequest("a/b", 1, "c1"));
        cola.Encolar(new PullRequest("a/b", 2, "c2"));

        var ejecutor = new EjecutorEspia { ReventarEnPr = 1 };
        var worker = new Worker(
            new RegistradorFalso<Worker>(),
            new ConfiguracionSondeo { IntervaloMinutos = 5, Repositorios = new[] { "a/b" } },
            ejecutor,
            new RelojQueNoEspera(),
            estado: null,
            cola: cola);

        await worker.AtenderAvisosAsync(CancellationToken.None);

        // El segundo se atiende igual: un aviso roto no puede parar la máquina.
        Assert.Contains(ejecutor.PorAviso, p => p.Numero == 2);
    }

    [Fact]
    public async Task SinCola_ElWorkerSigueFuncionando()
    {
        // El webhook es opcional: sin él, todo funciona como antes.
        var worker = new Worker(
            new RegistradorFalso<Worker>(),
            new ConfiguracionSondeo { IntervaloMinutos = 5, Repositorios = new[] { "a/b" } },
            new EjecutorEspia(),
            new RelojQueNoEspera());

        await worker.AtenderAvisosAsync(CancellationToken.None);
    }

    // ---------- Configuración ----------

    [Fact]
    public void HabilitadoSinSecreto_NoArranca()
    {
        // Es preferible no arrancar a exponer un disparador de gasto sin autenticar.
        var error = Assert.Throws<InvalidOperationException>(
            () => ConfiguracionWebhook.ValidarConfiguracion(
                new ConfiguracionWebhook { Habilitado = true }));

        Assert.Contains("Webhook:Secreto", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeshabilitadoSinSecreto_NoSeQueja()
    {
        // Apagado es el estado de fábrica y no exige configurar nada.
        ConfiguracionWebhook.ValidarConfiguracion(new ConfiguracionWebhook());
    }

    [Fact]
    public void PorDefecto_VieneApagadoYEnLoopback()
    {
        var config = new ConfiguracionWebhook();

        Assert.False(config.Habilitado);
        Assert.Equal("127.0.0.1", config.Direccion);
        Assert.Equal("/webhook/bitbucket", config.Ruta);
    }

    [Theory]
    [InlineData("no-es-una-ip")]
    [InlineData("")]
    public void ConDireccionInvalida_NoArranca(string direccion)
    {
        Assert.Throws<InvalidOperationException>(
            () => ConfiguracionWebhook.ValidarConfiguracion(new ConfiguracionWebhook
            {
                Habilitado = true,
                Secreto = Secreto,
                Direccion = direccion,
            }));
    }

    [Fact]
    public void ConRutaSinBarra_NoArranca()
    {
        Assert.Throws<InvalidOperationException>(
            () => ConfiguracionWebhook.ValidarConfiguracion(new ConfiguracionWebhook
            {
                Habilitado = true,
                Secreto = Secreto,
                Ruta = "webhook",
            }));
    }

    // ---------- Utilidades ----------

    private sealed class EjecutorEspia : IEjecutorVuelta
    {
        public List<PullRequest> PorAviso { get; } = new();
        public int ReventarEnPr { get; set; } = -1;

        public Task EjecutarAsync(CancellationToken cancelacion) => Task.CompletedTask;

        public Task RevisarPrAsync(PullRequest pr, CancellationToken cancelacion)
        {
            if (pr.Numero == ReventarEnPr)
            {
                throw new InvalidOperationException("fallo simulado revisando el aviso");
            }

            PorAviso.Add(pr);
            return Task.CompletedTask;
        }
    }

    private sealed class RelojQueNoEspera : IReloj
    {
        public Task EsperarAsync(TimeSpan intervalo, CancellationToken cancelacion) => Task.CompletedTask;
    }
}
