using System;
using System.Net;
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
/// Prueba el webhook levantando el servidor de verdad y hablándole por HTTP (C2).
///
/// Las demás pruebas ejercitan las piezas por separado; estas comprueban que el conjunto
/// escucha, autentica, interpreta y encola. Es el equivalente a la prueba de cableado que
/// faltaba en items anteriores.
/// </summary>
public sealed class WebhookExtremoAExtremoTests : IAsyncLifetime
{
    private const string Secreto = "secreto-de-prueba";

    private readonly ColaDeRevisiones _cola = new();
    private ServidorWebhook? _servidor;
    private HttpClient? _cliente;

    public async Task InitializeAsync()
    {
        _servidor = new ServidorWebhook(
            NullLogger<ServidorWebhook>.Instance,
            new ConfiguracionWebhook
            {
                Habilitado = true,
                Secreto = Secreto,
                // Puerto 0: lo elige el sistema, así dos pruebas no chocan.
                Puerto = 0,
            },
            _cola,
            new TraductorEventoPr(NullLogger<TraductorEventoPr>.Instance));

        await _servidor.StartAsync(CancellationToken.None);

        _cliente = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_servidor.PuertoActivo}"),
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    public async Task DisposeAsync()
    {
        _cliente?.Dispose();
        if (_servidor is not null)
        {
            await _servidor.StopAsync(CancellationToken.None);
            _servidor.Dispose();
        }
    }

    private static string Cuerpo(int numero) => $$"""
        {
          "repository": { "full_name": "equipo-a/repo-1" },
          "pullrequest": {
            "id": {{numero}},
            "title": "Arreglar el IVA",
            "links": { "html": { "href": "https://bitbucket.org/equipo-a/repo-1/pull-requests/{{numero}}" } },
            "source": { "commit": { "hash": "abc123" } },
            "destination": { "branch": { "name": "main" } }
          }
        }
        """;

    private static HttpRequestMessage Aviso(string cuerpo, string? firma = null, string? bearer = null)
    {
        var peticion = new HttpRequestMessage(HttpMethod.Post, "/webhook/bitbucket")
        {
            Content = new StringContent(cuerpo, Encoding.UTF8, "application/json"),
        };

        if (firma is not null)
        {
            peticion.Headers.TryAddWithoutValidation("X-Hub-Signature", "sha256=" + firma);
        }

        if (bearer is not null)
        {
            peticion.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        }

        return peticion;
    }

    private static string FirmaDe(string cuerpo)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secreto));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(cuerpo))).ToLowerInvariant();
    }

    [Fact]
    public async Task UnAvisoFirmadoAcabaEnLaCola()
    {
        string cuerpo = Cuerpo(42);

        var respuesta = await _cliente!.SendAsync(Aviso(cuerpo, firma: FirmaDe(cuerpo)));

        Assert.Equal(HttpStatusCode.Accepted, respuesta.StatusCode);
        Assert.True(_cola.IntentarSacar(out var pr));
        Assert.Equal(42, pr!.Numero);
        Assert.Equal("equipo-a/repo-1", pr.Repositorio);
    }

    [Fact]
    public async Task UnAvisoConTokenBearerTambienEntra()
    {
        var respuesta = await _cliente!.SendAsync(Aviso(Cuerpo(7), bearer: Secreto));

        Assert.Equal(HttpStatusCode.Accepted, respuesta.StatusCode);
        Assert.True(_cola.IntentarSacar(out var pr));
        Assert.Equal(7, pr!.Numero);
    }

    [Fact]
    public async Task UnAvisoSinCredencial_DevuelveNoAutorizadoYNoEncolaNada()
    {
        var respuesta = await _cliente!.SendAsync(Aviso(Cuerpo(42)));

        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
        Assert.Equal(0, _cola.Pendientes);
    }

    [Fact]
    public async Task UnAvisoConFirmaFalsa_DevuelveNoAutorizado()
    {
        var respuesta = await _cliente!.SendAsync(Aviso(Cuerpo(42), firma: new string('0', 64)));

        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
        Assert.Equal(0, _cola.Pendientes);
    }

    [Fact]
    public async Task OtraRuta_DevuelveNoEncontrado()
    {
        string cuerpo = Cuerpo(42);
        var peticion = new HttpRequestMessage(HttpMethod.Post, "/otra-cosa")
        {
            Content = new StringContent(cuerpo, Encoding.UTF8, "application/json"),
        };
        peticion.Headers.TryAddWithoutValidation("X-Hub-Signature", "sha256=" + FirmaDe(cuerpo));

        var respuesta = await _cliente!.SendAsync(peticion);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnGet_DevuelveMetodoNoPermitido()
    {
        var respuesta = await _cliente!.GetAsync("/webhook/bitbucket");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnEventoQueNoEsDePullRequest_SeAceptaSinEncolar()
    {
        // 200 a propósito: el aviso llegó bien, simplemente no nos interesa. Devolver un
        // error haría que Bitbucket lo reintentara sin necesidad.
        const string cuerpo = """{"repository":{"full_name":"equipo-a/repo-1"}}""";

        var respuesta = await _cliente!.SendAsync(Aviso(cuerpo, firma: FirmaDe(cuerpo)));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal(0, _cola.Pendientes);
    }

    [Fact]
    public async Task VariosAvisosSeguidos_SeEncolanTodos()
    {
        for (int i = 1; i <= 5; i++)
        {
            string cuerpo = Cuerpo(i);
            var respuesta = await _cliente!.SendAsync(Aviso(cuerpo, firma: FirmaDe(cuerpo)));
            Assert.Equal(HttpStatusCode.Accepted, respuesta.StatusCode);
        }

        Assert.Equal(5, _cola.Pendientes);
    }
}
