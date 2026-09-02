using System;
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
/// Tests para la elección del método de autenticación de Bitbucket (RV.4c):
/// - Basic con usuario + app password
/// - Bearer con token de workspace
/// - Validación al arrancar si la combinación es ambigua o vacía
/// - El token no aparece nunca en el log
/// </summary>
public class AutenticacionBitbucketTests
{
    private const string TestUsuario = "testuser";
    private const string TestClave = "supersecreto123";
    private const string TestToken = "tokenDeWorkspace_ABC123_secreto";

    /// <summary>
    /// Handler HTTP falso que registra las peticiones recibidas (para inspeccionar la
    /// cabecera Authorization) y responde con éxito.
    /// </summary>
    private class HandlerCaptura : HttpMessageHandler
    {
        public HttpRequestMessage? UltimaPeticion { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UltimaPeticion = request;
            var respuesta = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"values\":[]}", Encoding.UTF8, "application/json")
            };
            return Task.FromResult(respuesta);
        }
    }

    private static ClienteBitbucket CrearClienteBasica(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.bitbucket.org/")
        };
        var config = Options.Create(new ConfiguracionBitbucket
        {
            MetodoAutenticacion = MetodoAutenticacionBitbucket.Basica,
            Usuario = TestUsuario,
            ClaveAplicacion = TestClave
        });
        var traductor = new TraductorEventoPr(NullLogger<TraductorEventoPr>.Instance);
        return new ClienteBitbucket(httpClient, config, NullLogger<ClienteBitbucket>.Instance, traductor);
    }

    private static ClienteBitbucket CrearClienteToken(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.bitbucket.org/")
        };
        var config = Options.Create(new ConfiguracionBitbucket
        {
            MetodoAutenticacion = MetodoAutenticacionBitbucket.Token,
            Token = TestToken
        });
        var traductor = new TraductorEventoPr(NullLogger<TraductorEventoPr>.Instance);
        return new ClienteBitbucket(httpClient, config, NullLogger<ClienteBitbucket>.Instance, traductor);
    }

    [Fact]
    public async Task MetodoBasico_EnviaCabeceraAuthorizationBasic()
    {
        var handler = new HandlerCaptura();
        var cliente = CrearClienteBasica(handler);

        await cliente.ListarPrsAbiertos("workspace/repo");

        Assert.NotNull(handler.UltimaPeticion);
        var auth = handler.UltimaPeticion!.Headers.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Basic", auth!.Scheme);
        var esperado = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{TestUsuario}:{TestClave}"));
        Assert.Equal(esperado, auth.Parameter);
    }

    [Fact]
    public async Task MetodoToken_EnviaCabeceraAuthorizationBearer()
    {
        var handler = new HandlerCaptura();
        var cliente = CrearClienteToken(handler);

        await cliente.ListarPrsAbiertos("workspace/repo");

        Assert.NotNull(handler.UltimaPeticion);
        var auth = handler.UltimaPeticion!.Headers.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Bearer", auth!.Scheme);
        Assert.Equal(TestToken, auth.Parameter);
    }

    [Fact]
    public void ValidarConfiguracion_BasicaSinCredenciales_FallaConMensajeAccionable()
    {
        var configuracion = new ConfiguracionBitbucket
        {
            MetodoAutenticacion = MetodoAutenticacionBitbucket.Basica,
            Usuario = string.Empty,
            ClaveAplicacion = string.Empty
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ClienteBitbucket.ValidarConfiguracion(configuracion));

        Assert.Contains("Bitbucket", ex.Message);
        Assert.True(
            ex.Message.Contains("Basica", StringComparison.Ordinal)
            || ex.Message.Contains("Usuario", StringComparison.Ordinal),
            $"El mensaje debe guiar al usuario hacia la solución: {ex.Message}");
    }

    [Fact]
    public void ValidarConfiguracion_BasicaConTokenTambien_FallaConMensajeAccionable()
    {
        var configuracion = new ConfiguracionBitbucket
        {
            MetodoAutenticacion = MetodoAutenticacionBitbucket.Basica,
            Usuario = TestUsuario,
            ClaveAplicacion = TestClave,
            Token = TestToken
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ClienteBitbucket.ValidarConfiguracion(configuracion));

        Assert.Contains("Bitbucket", ex.Message);
        Assert.True(
            ex.Message.Contains("Token", StringComparison.Ordinal),
            $"El mensaje debe mencionar el campo Token: {ex.Message}");
    }

    [Fact]
    public void ValidarConfiguracion_TokenVacio_FallaConMensajeAccionable()
    {
        var configuracion = new ConfiguracionBitbucket
        {
            MetodoAutenticacion = MetodoAutenticacionBitbucket.Token,
            Token = string.Empty
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ClienteBitbucket.ValidarConfiguracion(configuracion));

        Assert.Contains("Bitbucket", ex.Message);
        Assert.True(
            ex.Message.Contains("Token", StringComparison.Ordinal),
            $"El mensaje debe mencionar el campo Token: {ex.Message}");
    }

    [Fact]
    public void ValidarConfiguracion_TokenConCredencialesBasicaTambien_FallaConMensajeAccionable()
    {
        var configuracion = new ConfiguracionBitbucket
        {
            MetodoAutenticacion = MetodoAutenticacionBitbucket.Token,
            Usuario = TestUsuario,
            ClaveAplicacion = TestClave,
            Token = TestToken
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ClienteBitbucket.ValidarConfiguracion(configuracion));

        Assert.Contains("Bitbucket", ex.Message);
        Assert.True(
            ex.Message.Contains("Usuario", StringComparison.Ordinal),
            $"El mensaje debe guiar a quitar las credenciales Basic: {ex.Message}");
    }

    [Fact]
    public async Task Token_NoApareceEnLog_TrasLlamadaCorrecta()
    {
        var handler = new HandlerCaptura();
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.bitbucket.org/")
        };
        var config = Options.Create(new ConfiguracionBitbucket
        {
            MetodoAutenticacion = MetodoAutenticacionBitbucket.Token,
            Token = TestToken
        });
        var logger = new RegistradorFalso<ClienteBitbucket>();
        var traductor = new TraductorEventoPr(NullLogger<TraductorEventoPr>.Instance);
        var cliente = new ClienteBitbucket(httpClient, config, logger, traductor);

        await cliente.ListarPrsAbiertos("workspace/repo");

        foreach (var mensaje in logger.Mensajes)
        {
            Assert.False(
                mensaje.Contains(TestToken, StringComparison.Ordinal),
                $"El token no debe aparecer en el log: {mensaje}");
        }
    }
}