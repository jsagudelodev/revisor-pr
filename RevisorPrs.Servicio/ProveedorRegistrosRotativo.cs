using System;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RevisorPrs.Servicio;

/// <summary>
/// Proveedor de <see cref="ILogger"/> que escribe a un fichero rotado por tamano (RV.21).
///
/// Hace dos cosas que NO se permiten en el log:
/// - sustituye los valores sensibles declarados en la configuracion por una marca fija
///   (mismo criterio que el resto del proyecto: <see cref="SaneadorSecretos"/>);
/// - bloquea cualquier mensaje cuyo TEXTO contenga un diff de Bitbucket, para que
///   el contenido de los PRs no acabe nunca en disco.
///
/// El saneado se aplica a los argumentos de la entrada de log, no al mensaje formateado
/// resultante: asi secretos que viajen como parametro siguen ocultos aunque el template
/// del log no los muestre de forma obvia.
/// </summary>
public sealed class ProveedorRegistrosRotativo : ILoggerProvider
{
    private readonly RotadorRegistros _rotador;
    private readonly SaneadorSecretos _saneador;
    private readonly object _candadoEscritura = new();
    private readonly TextWriter? _consola;

    /// <summary>
    /// Marca que sustituye al contenido de un diff si alguien intenta loguearlo.
    /// Es estable para que los tests puedan assertar contra ella.
    /// </summary>
    public const string MarcaBloqueoDiff = "[REDACTADO: contenido de diff omitido]";

    /// <summary>
    /// Crea el proveedor a partir de la configuracion. Si <paramref name="saneador"/>
    /// es null no se enmascaran secretos (util en tests que quieren ver el texto crudo).
    /// </summary>
    public ProveedorRegistrosRotativo(
        ConfiguracionRegistro configuracion,
        SaneadorSecretos? saneador = null,
        TextWriter? consola = null)
        : this(
            new RotadorRegistros(configuracion),
            saneador,
            consola)
    {
    }

    /// <summary>
    /// Crea el proveedor con un rotador ya construido (util en tests para inyectar
    /// funciones de tamano y forzar rotaciones deterministas).
    /// </summary>
    public ProveedorRegistrosRotativo(
        RotadorRegistros rotador,
        SaneadorSecretos? saneador = null,
        TextWriter? consola = null)
    {
        _rotador = rotador ?? throw new ArgumentNullException(nameof(rotador));
        _saneador = saneador ?? SaneadorSecretos.Ninguno;
        _consola = consola;
    }

    /// <summary>
    /// Ruta del fichero activo donde se escribe el log.
    /// </summary>
    public string RutaActiva => _rotador.RutaActiva;

    /// <summary>
    /// Rotador subyacente, expuesto para los tests.
    /// </summary>
    public RotadorRegistros Rotador => _rotador;

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new RegistroRotativo(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
        // Nada que liberar: el fichero lo mantiene abierto el sistema hasta el fin del proceso.
    }

    /// <summary>
    /// Escribe una linea ya saneada en el fichero, rotando antes si toca.
    /// </summary>
    internal void Escribir(string linea)
    {
        lock (_candadoEscritura)
        {
            _rotador.RotarSiEsNecesario();

            string? directorio = Path.GetDirectoryName(_rotador.RutaActiva);
            if (!string.IsNullOrEmpty(directorio) && !Directory.Exists(directorio))
            {
                Directory.CreateDirectory(directorio);
            }

            File.AppendAllText(_rotador.RutaActiva, linea + Environment.NewLine, Encoding.UTF8);

            if (_consola is not null)
            {
                _consola.WriteLine(linea);
            }
        }
    }

    private sealed class RegistroRotativo : ILogger
    {
        private readonly ProveedorRegistrosRotativo _proveedor;
        private readonly string _categoria;

        public RegistroRotativo(ProveedorRegistrosRotativo proveedor, string categoria)
        {
            _proveedor = proveedor;
            _categoria = categoria;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => AmbitoNulo.Instancia;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            if (formatter is null)
            {
                return;
            }

            string mensajeCrudo = formatter(state, exception);
            string mensajeSaneado = _proveedor._saneador.Sanear(mensajeCrudo);
            if (ContieneDiff(mensajeSaneado))
            {
                mensajeSaneado = MarcaBloqueoDiff;
            }

            string? excepcionSaneada = exception is null
                ? null
                : _proveedor._saneador.Sanear(exception.ToString());
            if (excepcionSaneada is not null && ContieneDiff(excepcionSaneada))
            {
                excepcionSaneada = MarcaBloqueoDiff;
            }

            string marcaTiempo = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", System.Globalization.CultureInfo.InvariantCulture);
            string linea = $"[{marcaTiempo}] [{logLevel}] {_categoria}: {mensajeSaneado}";
            if (excepcionSaneada is not null)
            {
                linea += Environment.NewLine + excepcionSaneada;
            }

            _proveedor.Escribir(linea);
        }

        private static bool ContieneDiff(string texto)
        {
            if (string.IsNullOrEmpty(texto))
            {
                return false;
            }
            // Marcas tipicas de un diff de Bitbucket/Git. Si estan en el mensaje,
            // mejor reemplazar el contenido entero a sustituir linea por linea.
            return texto.Contains("diff --git ", StringComparison.Ordinal)
                || texto.Contains("@@ ", StringComparison.Ordinal) && texto.Contains("\n+", StringComparison.Ordinal)
                || texto.Contains("--- a/", StringComparison.Ordinal) && texto.Contains("\n+++ b/", StringComparison.Ordinal);
        }

        private sealed class AmbitoNulo : IDisposable
        {
            public static AmbitoNulo Instancia { get; } = new();
            public void Dispose() { }
        }
    }
}