using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RevisorPrs.Servicio;
using Xunit;

namespace RevisorPrs.Tests;

/// <summary>
/// Pruebas de que las llamadas a Bitbucket responden a la parada del servicio.
///
/// <see cref="ClienteBitbucket.EnviarConReintentos"/> usaba un
/// <c>CancellationToken.None</c> fijo, así que ni el listado, ni la descarga del diff,
/// ni la publicación de comentarios se enteraban de que el servicio se estaba parando:
/// una vuelta podía seguir llamando a la API durante todo el apagado, y las esperas
/// entre reintentos no se podían abortar.
///
/// El token también sirve para distinguir dos cosas que .NET representa igual: una
/// parada real del servicio (el token está cancelado, hay que abortar) y el tiempo de
/// espera agotado de HttpClient (el token NO está cancelado, es un fallo de red y se
/// reintenta).
/// </summary>
public class CancelacionClienteTests
{
    private static ClienteBitbucket CrearCliente(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.bitbucket.org/"),
        };

        var config = Options.Create(new ConfiguracionBitbucket
        {
            Usuario = "usuario",
            ClaveAplicacion = "clave",
        });

        return new ClienteBitbucket(
            httpClient,
            config,
            NullLogger<ClienteBitbucket>.Instance,
            new TraductorEventoPr(NullLogger<TraductorEventoPr>.Instance));
    }

    [Fact]
    public async Task ObtenerDiff_ConTokenYaCancelado_NoLlegaALaRed()
    {
        var handler = new HandlerContador(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("diff", Encoding.UTF8, "text/plain"),
        });
        var cliente = CrearCliente(handler);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cliente.ObtenerDiff("workspace/repo", 1, cts.Token));

        Assert.Equal(0, handler.Llamadas);
    }

    [Fact]
    public async Task ListarPrsAbiertos_ConTokenYaCancelado_NoLlegaALaRed()
    {
        var handler = new HandlerContador(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"values\":[]}", Encoding.UTF8, "application/json"),
        });
        var cliente = CrearCliente(handler);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cliente.ListarPrsAbiertos("workspace/repo", cts.Token));

        Assert.Equal(0, handler.Llamadas);
    }

    [Fact]
    public async Task PublicarComentario_ConCancelacionAMitad_AbortaSinAgotarLosReintentos()
    {
        // La API responde 500, así que el cliente entraría a reintentar. El servicio se
        // para durante la primera espera: la operación debe abortar en ese momento.
        var handler = new HandlerContador(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var cliente = CrearCliente(handler);

        using var cts = new CancellationTokenSource();
        cliente.EsperarEntreReintentos = (_, token) =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        var hallazgo = new Hallazgo("src/A.cs", 1, "error", "resumen", "detalle");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cliente.PublicarComentario("workspace/repo", 1, hallazgo, cts.Token));

        // Solo el primer intento: no se consumieron los 3 del tope.
        Assert.Equal(1, handler.Llamadas);
    }

    [Fact]
    public async Task ObtenerDiff_ConTiempoDeEsperaAgotado_ReintentaEnVezDeAbortar()
    {
        // HttpClient señala su propio tiempo de espera con una TaskCanceledException,
        // igual que una cancelación. Como el token NO está cancelado, esto es un fallo
        // de red: hay que reintentar, no abandonar. Al tercer intento responde bien.
        var intentos = 0;
        var handler = new HandlerContador(() =>
        {
            intentos++;
            if (intentos < 3)
            {
                throw new TaskCanceledException("tiempo de espera simulado");
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("el diff", Encoding.UTF8, "text/plain"),
            };
        });

        var cliente = CrearCliente(handler);
        cliente.EsperarEntreReintentos = (_, _) => Task.CompletedTask;

        var diff = await cliente.ObtenerDiff("workspace/repo", 1, CancellationToken.None);

        Assert.Equal("el diff", diff);
        Assert.Equal(3, handler.Llamadas);
    }

    private sealed class HandlerContador : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respuesta;

        public HandlerContador(Func<HttpResponseMessage> respuesta) => _respuesta = respuesta;

        public int Llamadas { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Llamadas++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_respuesta());
        }
    }
}
