using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RevisorPrs.Servicio;
using Xunit;

namespace RevisorPrs.Tests;

/// <summary>
/// Tests de reintentos y límite de tasa contra la API de Bitbucket (RV.7).
/// </summary>
public class ReintentosTests
{
    /// <summary>
    /// Handler HTTP falso que encola respuestas y cuenta cuántas veces se invoca SendAsync.
    /// </summary>
    private class HandlerFalso : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _respuestas = new();
        private readonly ConcurrentBag<HttpRequestMessage> _peticiones = new();
        private readonly ConcurrentBag<Exception> _excepciones = new();
        public int Llamadas => _peticiones.Count + _excepciones.Count;

        public void Encolar(HttpStatusCode codigo, string cuerpo = "")
        {
            _respuestas.Enqueue(new HttpResponseMessage(codigo)
            {
                Content = new StringContent(cuerpo, Encoding.UTF8, "application/json")
            });
        }

        public void EncolarExcepcion(HttpRequestException ex)
        {
            _excepcionesEncolar.Enqueue(ex);
        }

        private readonly ConcurrentQueue<HttpRequestException?> _excepcionesEncolar = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _peticiones.Add(request);

            if (_excepcionesEncolar.TryDequeue(out var ex) && ex != null)
            {
                _excepciones.Add(ex);
                throw ex;
            }

            if (_respuestas.Count == 0)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(_respuestas.Dequeue());
        }
    }

    private static ClienteBitbucket CrearCliente(
        HandlerFalso handler,
        ConfiguracionBitbucket? config = null,
        ILogger<ClienteBitbucket>? logger = null)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.bitbucket.org/")
        };

        var cfg = config ?? new ConfiguracionBitbucket
        {
            Usuario = "testuser",
            ClaveAplicacion = "testpass",
            IntentosMaximos = 3
        };

        var traductor = new TraductorEventoPr(NullLogger<TraductorEventoPr>.Instance);
        var cliente = new ClienteBitbucket(
            httpClient,
            Options.Create(cfg),
            logger ?? NullLogger<ClienteBitbucket>.Instance,
            traductor);

        // Anulamos la espera para que los tests no tarden segundos.
        cliente.EsperarEntreReintentos = (_, _) => Task.CompletedTask;

        return cliente;
    }

    private static ILogger<ClienteBitbucket> CrearLoggerCaptura(out CapturadorLog<ClienteBitbucket> capturador)
    {
        capturador = new CapturadorLog<ClienteBitbucket>();
        return capturador;
    }

    [Fact]
    public async Task Reintentos_Con429Luego429Luego200_DevuelveElResultado()
    {
        // Arrange
        var handler = new HandlerFalso();
        handler.Encolar(HttpStatusCode.TooManyRequests); // 429
        handler.Encolar(HttpStatusCode.TooManyRequests); // 429
        handler.Encolar(HttpStatusCode.OK, "{\"values\":[]}"); // 200

        var cliente = CrearCliente(handler);

        // Act
        var resultado = await cliente.ListarPrsAbiertos("workspace/repo");

        // Assert
        Assert.NotNull(resultado);
        Assert.Empty(resultado);
        Assert.Equal(3, handler.Llamadas);
    }

    [Fact]
    public async Task Reintentos_AlAgotarElTope_RegistraErrorAccionableSinLanzar()
    {
        // Arrange: todas las respuestas son 500 -> agotará el tope (3) sin lanzar.
        var handler = new HandlerFalso();
        handler.Encolar(HttpStatusCode.InternalServerError);
        handler.Encolar(HttpStatusCode.InternalServerError);
        handler.Encolar(HttpStatusCode.InternalServerError);

        var logger = CrearLoggerCaptura(out var capturador);
        var cliente = CrearCliente(handler, logger: logger);

        // Act
        var resultado = await cliente.ListarPrsAbiertos("workspace/repo");

        // Assert: no lanza, devuelve lista vacía y registra error accionable.
        Assert.NotNull(resultado);
        Assert.Empty(resultado);
        Assert.Equal(3, handler.Llamadas);

        var mensajeError = capturador.ObtenerMensajesNivel(LogLevel.Error);
        Assert.Contains(mensajeError, m =>
            m.Contains("se agotaron los reintentos", System.StringComparison.OrdinalIgnoreCase)
            && m.Contains("workspace/repo", System.StringComparison.OrdinalIgnoreCase)
            && m.Contains("500", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Reintentos_Con404_NoReintenta()
    {
        // Arrange
        var handler = new HandlerFalso();
        handler.Encolar(HttpStatusCode.NotFound); // 404

        var cliente = CrearCliente(handler);

        // Act
        var diff = await cliente.ObtenerDiff("workspace/repo", 42);

        // Assert: solo una llamada, y devuelve vacío porque 404 no es éxito.
        Assert.Empty(diff);
        Assert.Equal(1, handler.Llamadas);
    }

    /// <summary>
    /// Logger mínimo que guarda mensajes para inspección en tests.
    /// </summary>
    private class CapturadorLog<T> : ILogger<T>
    {
        private readonly ConcurrentBag<(LogLevel nivel, string mensaje)> _entradas = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _entradas.Add((logLevel, formatter(state, exception)));
        }

        public System.Collections.Generic.List<string> ObtenerMensajesNivel(LogLevel nivel)
        {
            return _entradas.Where(e => e.nivel == nivel).Select(e => e.mensaje).ToList();
        }

        private class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}