using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace RevisorPrs.Tests;

/// <summary>
/// Implementación falsa de ILogger que captura los mensajes de log para inspección en pruebas.
/// </summary>
public class RegistradorFalso<T> : ILogger<T>
{
    private readonly List<string> _mensajes = new();

    public IReadOnlyList<string> Mensajes => _mensajes;

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var mensaje = formatter(state, exception);
        _mensajes.Add($"{logLevel}: {mensaje}");
    }

    private class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose() { }
    }
}