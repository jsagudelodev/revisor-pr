using System;
using System.Collections.Generic;

namespace RevisorPrs.Servicio;

/// <summary>
/// Error registrado por el servicio, con la marca de tiempo UTC en la que se produjo.
/// El mensaje ya viene saneado de secretos por quien lo registra.
/// </summary>
/// <param name="Utc">Momento UTC del error.</param>
/// <param name="Mensaje">Texto legible del error.</param>
public record ErrorRegistrado(DateTimeOffset Utc, string Mensaje);

/// <summary>
/// Fotografía inmutable del estado observable del servicio, pensada para que el
/// endpoint /estado la serialice sin tener que bloquear el estado interno.
/// </summary>
public record InstanteEstado(
    DateTimeOffset? UltimaVueltaUtc,
    DateTimeOffset? ProximaVueltaUtc,
    int RevisadosUltimaVuelta,
    int RevisadosAcumulados,
    int OmitidosUltimaVuelta,
    int FallidosUltimaVuelta,
    int FallidosAcumulados,
    IReadOnlyList<ErrorRegistrado> UltimosErrores);

/// <summary>
/// Estado observable del servicio de sondeo: qué hizo en la última vuelta, cuántos
/// pull requests se han revisado, cuándo toca el próximo sondeo y los últimos errores.
///
/// Es el único punto donde el sondeo publica lo que va pasando, de modo que el
/// endpoint /estado (RV.20) pueda responder sin consultar a nadie más. Todos sus
/// métodos son seguros para llamadas concurrentes: el sondeo escribe desde su hilo
/// y el endpoint lee desde el hilo HTTP.
/// </summary>
public sealed class EstadoServicio
{
    /// <summary>
    /// Máximo de errores que se conservan en memoria. Los más antiguos se descartan
    /// para que un fallo persistente no haga crecer el estado sin límite.
    /// </summary>
    public const int MaximoErroresConservados = 20;

    private readonly Func<DateTimeOffset> _ahora;
    private readonly object _candado = new();
    private readonly List<ErrorRegistrado> _errores = new();

    private DateTimeOffset? _ultimaVueltaUtc;
    private DateTimeOffset? _proximaVueltaUtc;
    private int _revisadosUltimaVuelta;
    private int _revisadosAcumulados;
    private int _omitidosUltimaVuelta;
    private int _fallidosUltimaVuelta;
    private int _fallidosAcumulados;

    public EstadoServicio(Func<DateTimeOffset>? ahora = null)
    {
        _ahora = ahora ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Deja constancia de una vuelta de sondeo terminada: acumula los revisados y
    /// reemplaza las cifras correspondientes a la última vuelta.
    /// </summary>
    public void RegistrarVuelta(ResultadoVuelta resultado)
    {
        if (resultado is null)
        {
            throw new ArgumentNullException(nameof(resultado));
        }

        lock (_candado)
        {
            _ultimaVueltaUtc = _ahora();
            _revisadosUltimaVuelta = resultado.PrsRevisados;
            _omitidosUltimaVuelta = resultado.PrsOmitidos;
            _fallidosUltimaVuelta = resultado.PrsFallidos;
            _revisadosAcumulados += resultado.PrsRevisados;
            _fallidosAcumulados += resultado.PrsFallidos;
        }
    }

    /// <summary>
    /// Anuncia cuándo se espera la próxima vuelta, sumando el intervalo de sondeo al
    /// momento actual. Se llama justo después de terminar una vuelta y antes de dormir.
    /// </summary>
    public void AnunciarProximoSondeo(TimeSpan intervalo)
    {
        lock (_candado)
        {
            _proximaVueltaUtc = _ahora() + intervalo;
        }
    }

    /// <summary>
    /// Guarda un error para que el endpoint /estado lo exponga. El mensaje se recorta
    /// para que una excepción muy verbosa no llene la respuesta.
    /// </summary>
    public void RegistrarError(string mensaje)
    {
        string texto = string.IsNullOrWhiteSpace(mensaje) ? "error sin detalle" : mensaje.Trim();
        const int limiteCaracteres = 500;
        if (texto.Length > limiteCaracteres)
        {
            texto = texto.Substring(0, limiteCaracteres) + "…";
        }

        lock (_candado)
        {
            _errores.Add(new ErrorRegistrado(_ahora(), texto));
            int sobrantes = _errores.Count - MaximoErroresConservados;
            if (sobrantes > 0)
            {
                _errores.RemoveRange(0, sobrantes);
            }
        }
    }

    /// <summary>
    /// Devuelve una copia inmutable del estado en este instante.
    /// </summary>
    public InstanteEstado Capturar()
    {
        lock (_candado)
        {
            return new InstanteEstado(
                UltimaVueltaUtc: _ultimaVueltaUtc,
                ProximaVueltaUtc: _proximaVueltaUtc,
                RevisadosUltimaVuelta: _revisadosUltimaVuelta,
                RevisadosAcumulados: _revisadosAcumulados,
                OmitidosUltimaVuelta: _omitidosUltimaVuelta,
                FallidosUltimaVuelta: _fallidosUltimaVuelta,
                FallidosAcumulados: _fallidosAcumulados,
                UltimosErrores: _errores.ToArray());
        }
    }
}