using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RevisorPrs.Servicio;

/// <summary>
/// Filtra los hallazgos devueltos por el LLM para descartar ruido antes de publicarlos
/// como comentario en el pull request.
///
/// Reglas de filtrado (RV.11):
///   - Severidad por debajo del umbral configurado en <see cref="ConfiguracionLlm.SeveridadMinima"/>
///     se descarta. Severidades reconocidas (de menor a mayor): "baja", "media", "alta".
///   - Hallazgos que apuntan a una línea NO presente en el diff se descartan
///     (comentar una línea que el PR no toca es ruido puro).
///   - Un hallazgo SIN línea (<see cref="Hallazgo.Linea"/> == null) NO se descarta por
///     la regla anterior: se conserva como comentario general.
///
/// Cada descarte se registra en el log con el motivo correspondiente.
/// </summary>
public class FiltroRuido
{
    private readonly ConfiguracionLlm _config;
    private readonly ILogger<FiltroRuido> _logger;

    /// <summary>
    /// Mapa de severidad a peso ordinal. Severidades desconocidas se tratan
    /// como la más baja posible para que cualquier umbral activo las descarte.
    /// </summary>
    private static readonly Dictionary<string, int> PesoSeveridad = new(StringComparer.OrdinalIgnoreCase)
    {
        ["baja"] = 0,
        ["media"] = 1,
        ["alta"] = 2,
    };

    public FiltroRuido(IOptions<ConfiguracionLlm> config, ILogger<FiltroRuido> logger)
    {
        if (config is null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        _config = config.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Filtra los <paramref name="hallazgos"/> aplicando las reglas de descarte.
    /// Devuelve únicamente los hallazgos que sobreviven al filtro.
    /// </summary>
    public IReadOnlyList<Hallazgo> Filtrar(IReadOnlyList<Hallazgo> hallazgos, string diff)
    {
        if (hallazgos is null)
        {
            throw new ArgumentNullException(nameof(hallazgos));
        }

        if (diff is null)
        {
            throw new ArgumentNullException(nameof(diff));
        }

        var umbralPeso = ObtenerPesoUmbral(_config.SeveridadMinima);
        // Conjunto de líneas presentes en el diff (positivo y negativo).
        // Se construye una sola vez para todos los hallazgos.
        var lineasEnDiff = ExtraerLineasDelDiff(diff);

        var conservados = new List<Hallazgo>(hallazgos.Count);
        foreach (var hallazgo in hallazgos)
        {
            var motivo = EvaluarDescarte(hallazgo, umbralPeso, lineasEnDiff);
            if (motivo is null)
            {
                conservados.Add(hallazgo);
            }
            else
            {
                _logger.LogInformation(
                    "Hallazgo descartado ({Motivo}): {Archivo}:{Linea} [{Severidad}] {Resumen}",
                    motivo,
                    hallazgo.Archivo,
                    hallazgo.Linea?.ToString() ?? "(sin línea)",
                    hallazgo.Severidad,
                    hallazgo.Resumen);
            }
        }

        return conservados;
    }

    /// <summary>
    /// Devuelve el motivo de descarte si el hallazgo debe descartarse, o null
    /// si debe conservarse.
    /// </summary>
    private static string? EvaluarDescarte(Hallazgo hallazgo, int? umbralPeso, HashSet<int> lineasEnDiff)
    {
        // Regla 1: severidad por debajo del umbral.
        if (umbralPeso.HasValue)
        {
            var pesoHallazgo = PesoSeveridad.TryGetValue(hallazgo.Severidad, out var p) ? p : -1;
            if (pesoHallazgo < umbralPeso.Value)
            {
                return "severidad por debajo del umbral";
            }
        }

        // Regla 2: la línea apuntada no está en el diff.
        // Si el hallazgo NO tiene línea (Linea == null), NO se descarta por esta regla.
        if (hallazgo.Linea.HasValue)
        {
            if (!lineasEnDiff.Contains(hallazgo.Linea.Value))
            {
                return "línea fuera del diff";
            }
        }

        return null;
    }

    /// <summary>
    /// Traduce la severidad textual del umbral a su peso ordinal. Si el umbral está
    /// vacío o no se reconoce, se devuelve null (sin filtrado por severidad).
    /// </summary>
    private static int? ObtenerPesoUmbral(string severidadMinima)
    {
        if (string.IsNullOrWhiteSpace(severidadMinima))
        {
            return null;
        }

        return PesoSeveridad.TryGetValue(severidadMinima.Trim(), out var peso) ? peso : null;
    }

    /// <summary>
    /// Extrae todos los números de línea que aparecen en cabeceras de hunks del diff
    /// (líneas "@@ -a,b +c,d @@"). Se conservan tanto las líneas del archivo viejo
    /// (a) como del nuevo (c): un hallazgo puede referirse a cualquiera de los dos
    /// contextos. Las líneas explícitamente añadidas (+) o eliminadas (-) NO se
    /// cuentan: una línea removida deja de existir en el nuevo archivo, pero el LLM
    /// puede legítimamente comentarla porque el PR sí la toca.
    /// </summary>
    private static HashSet<int> ExtraerLineasDelDiff(string diff)
    {
        var lineas = new HashSet<int>();
        foreach (var linea in diff.Split('\n'))
        {
            if (!linea.StartsWith("@@", StringComparison.Ordinal))
            {
                continue;
            }

            // Formato típico: "@@ -<oldStart>[,<oldCount>] +<newStart>[,<newCount>] @@"
            var partes = linea.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var parte in partes)
            {
                if (parte.StartsWith("+", StringComparison.Ordinal) && parte.Length > 1)
                {
                    var numero = ExtraerNumeroInicial(parte.AsSpan(1));
                    if (numero.HasValue)
                    {
                        lineas.Add(numero.Value);
                    }
                }
                else if (parte.StartsWith("-", StringComparison.Ordinal) && parte.Length > 1)
                {
                    var numero = ExtraerNumeroInicial(parte.AsSpan(1));
                    if (numero.HasValue)
                    {
                        lineas.Add(numero.Value);
                    }
                }
            }
        }

        return lineas;
    }

    private static int? ExtraerNumeroInicial(ReadOnlySpan<char> texto)
    {
        var i = 0;
        while (i < texto.Length && char.IsDigit(texto[i]))
        {
            i++;
        }

        if (i == 0)
        {
            return null;
        }

        return int.Parse(texto.Slice(0, i));
    }
}