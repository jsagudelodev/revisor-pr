using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RevisorPrs.Servicio;

/// <summary>
/// Filtra los hallazgos devueltos por el LLM para descartar ruido antes de publicarlos
/// como comentario en el pull request.
///
/// Reglas de filtrado (RV.11):
///   - Severidad por debajo del umbral configurado en <see cref="ConfiguracionLlm.SeveridadMinima"/>
///     se descarta. Se reconocen dos vocabularios equivalentes (de menor a mayor):
///     "info"/"warning"/"error", que es lo que devuelve el modelo, y "baja"/"media"/"alta",
///     que es como se documenta el umbral en la configuración.
///   - Hallazgos que apuntan a una línea que SU ARCHIVO no toca en el diff se descartan
///     (comentar una línea que el PR no cambia es ruido puro).
///   - Un hallazgo SIN línea (<see cref="Hallazgo.Linea"/> == null) NO se descarta por
///     la regla anterior: se conserva como comentario general.
///
/// Cada descarte se registra en el log con el motivo correspondiente.
/// </summary>
public class FiltroRuido
{
    private readonly ConfiguracionLlm _config;
    private readonly ILogger<FiltroRuido> _logger;

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

        // Tramos que el diff toca, agrupados POR ARCHIVO. Se construyen una sola vez
        // para todos los hallazgos.
        var tramosEnDiff = MapaDeTramos.Construir(diff);

        var conservados = new List<Hallazgo>(hallazgos.Count);
        foreach (var hallazgo in hallazgos)
        {
            var motivo = EvaluarDescarte(hallazgo, umbralPeso, tramosEnDiff);
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
    private static string? EvaluarDescarte(
        Hallazgo hallazgo,
        int? umbralPeso,
        MapaDeTramos tramosEnDiff)
    {
        // Regla 1: severidad por debajo del umbral.
        if (umbralPeso.HasValue)
        {
            // Una severidad que no se reconoce pesa menos que cualquier umbral activo.
            var pesoHallazgo = Severidades.Peso(hallazgo.Severidad) ?? -1;
            if (pesoHallazgo < umbralPeso.Value)
            {
                return "severidad por debajo del umbral";
            }
        }

        // Regla 2: el hallazgo tiene que hablar de un archivo que el pull request toca.
        // Ademas de ruido, un hallazgo sobre otro archivo es la senal de que el modelo se
        // ha ido del guion: comprobarlo acota el dano de una inyeccion sin depender de
        // que el modelo obedezca.
        if (!tramosEnDiff.ConoceElArchivo(hallazgo.Archivo))
        {
            return "archivo fuera del diff";
        }

        // Regla 3: la línea apuntada no está entre las que el diff cambia EN ESE ARCHIVO.
        // Si el hallazgo NO tiene línea (Linea == null), NO se descarta por esta regla:
        // es un comentario general sobre un archivo que el PR sí toca.
        if (hallazgo.Linea.HasValue)
        {
            if (!tramosEnDiff.Contiene(hallazgo.Archivo, hallazgo.Linea.Value))
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

        return Severidades.Peso(severidadMinima);
    }

    /// <summary>
    /// Tramos de líneas que el diff toca, agrupados por archivo.
    /// </summary>
    /// <remarks>
    /// Antes se acumulaban en una lista única para todo el diff, y eso dejaba el filtro
    /// casi inofensivo: como los hunks suelen empezar en números bajos y solaparse entre
    /// archivos, un hallazgo en <c>B.cs:11</c> pasaba porque <c>A.cs</c> tenía un hunk
    /// que cubría la línea 11. En un pull request de varios ficheros no descartaba nada.
    /// </remarks>
    private sealed class MapaDeTramos
    {
        private readonly Dictionary<string, List<(int Inicio, int Fin)>> _porArchivo =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Tramos leídos antes de encontrar ninguna cabecera de archivo. Un diff de
        /// Bitbucket siempre las trae, así que esto solo se llena con entradas
        /// malformadas; en ese caso valen para cualquier archivo, porque ante un diff
        /// que no sabemos interpretar es preferible no descartar nada.
        /// </summary>
        private readonly List<(int Inicio, int Fin)> _sinArchivo = new();

        public static MapaDeTramos Construir(string diff)
        {
            var mapa = new MapaDeTramos();
            string? archivoNuevo = null;
            string? archivoViejo = null;

            foreach (var linea in diff.Split('\n'))
            {
                if (linea.StartsWith("diff --git ", StringComparison.Ordinal))
                {
                    (archivoViejo, archivoNuevo) = LeerCabeceraGit(linea);
                    continue;
                }

                if (linea.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    // Más fiable que "diff --git" cuando hay renombrados.
                    archivoNuevo = NormalizarRuta(linea.Substring(4)) ?? archivoNuevo;
                    continue;
                }

                if (linea.StartsWith("--- ", StringComparison.Ordinal))
                {
                    archivoViejo = NormalizarRuta(linea.Substring(4)) ?? archivoViejo;
                    continue;
                }

                if (!linea.StartsWith("@@", StringComparison.Ordinal))
                {
                    continue;
                }

                // Formato típico: "@@ -<inicioViejo>[,<cuenta>] +<inicioNuevo>[,<cuenta>] @@"
                foreach (var parte in linea.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (parte.Length <= 1 || (parte[0] != '+' && parte[0] != '-'))
                    {
                        continue;
                    }

                    var tramo = InterpretarTramo(parte.AsSpan(1));
                    if (!tramo.HasValue)
                    {
                        continue;
                    }

                    // El tramo "+" habla del archivo nuevo y el "-" del viejo. Con un
                    // renombrado son rutas distintas y el modelo puede citar cualquiera.
                    string? destino = parte[0] == '+' ? archivoNuevo : archivoViejo;
                    mapa.Anadir(destino, tramo.Value);
                }
            }

            return mapa;
        }

        /// <summary>
        /// Indica si el diff toca ese archivo.
        /// </summary>
        /// <remarks>
        /// Con un diff que no supimos atribuir a ningún archivo devolvemos siempre true:
        /// ante algo que no entendemos preferimos no descartar nada, igual que en el
        /// resto del filtro.
        /// </remarks>
        public bool ConoceElArchivo(string? archivo)
        {
            if (_porArchivo.Count == 0)
            {
                return true;
            }

            string? ruta = NormalizarRuta(archivo);
            if (ruta is null)
            {
                return false;
            }

            if (_porArchivo.ContainsKey(ruta))
            {
                return true;
            }

            string nombre = NombreDeArchivo(ruta);
            foreach (var clave in _porArchivo.Keys)
            {
                if (NombreDeArchivo(clave).Equals(nombre, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Indica si la línea señalada cae dentro de algún tramo DEL ARCHIVO indicado.
        /// </summary>
        public bool Contiene(string? archivo, int linea)
        {
            if (_sinArchivo.Count > 0 && Cae(linea, _sinArchivo))
            {
                return true;
            }

            string? ruta = NormalizarRuta(archivo);
            if (ruta is null)
            {
                return false;
            }

            if (_porArchivo.TryGetValue(ruta, out var tramos) && Cae(linea, tramos))
            {
                return true;
            }

            // El modelo puede citar solo el nombre del archivo en vez de la ruta
            // completa. Si no hay ambigüedad, lo damos por bueno.
            string nombre = NombreDeArchivo(ruta);
            List<(int Inicio, int Fin)>? unico = null;
            foreach (var par in _porArchivo)
            {
                if (!NombreDeArchivo(par.Key).Equals(nombre, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (unico is not null)
                {
                    // Dos archivos con el mismo nombre en carpetas distintas: no adivinamos.
                    return false;
                }

                unico = par.Value;
            }

            return unico is not null && Cae(linea, unico);
        }

        private void Anadir(string? archivo, (int Inicio, int Fin) tramo)
        {
            if (archivo is null)
            {
                _sinArchivo.Add(tramo);
                return;
            }

            if (!_porArchivo.TryGetValue(archivo, out var tramos))
            {
                tramos = new List<(int Inicio, int Fin)>();
                _porArchivo[archivo] = tramos;
            }

            tramos.Add(tramo);
        }

        private static bool Cae(int linea, List<(int Inicio, int Fin)> tramos)
        {
            foreach (var (inicio, fin) in tramos)
            {
                if (linea >= inicio && linea <= fin)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// "diff --git a/ruta b/ruta" trae la ruta vieja y la nueva.
        /// </summary>
        private static (string? Viejo, string? Nuevo) LeerCabeceraGit(string linea)
        {
            var resto = linea.Substring("diff --git ".Length).Trim();
            int corte = resto.IndexOf(" b/", StringComparison.Ordinal);
            if (corte < 0)
            {
                var suelta = NormalizarRuta(resto);
                return (suelta, suelta);
            }

            return (NormalizarRuta(resto.Substring(0, corte)),
                    NormalizarRuta(resto.Substring(corte + 1)));
        }

        /// <summary>
        /// Deja la ruta comparable: sin prefijos "a/" o "b/", sin barra inicial, con
        /// separadores unificados y sin la marca de tiempo que git añade tras un
        /// tabulador. Devuelve null para "/dev/null" (archivo creado o borrado) y para
        /// lo vacío.
        /// </summary>
        private static string? NormalizarRuta(string? ruta)
        {
            if (string.IsNullOrWhiteSpace(ruta))
            {
                return null;
            }

            var texto = ruta.Trim();

            int tabulador = texto.IndexOf('\t');
            if (tabulador >= 0)
            {
                texto = texto.Substring(0, tabulador).Trim();
            }

            texto = texto.Replace('\\', '/');

            if (texto.Equals("/dev/null", StringComparison.Ordinal))
            {
                return null;
            }

            if (texto.StartsWith("a/", StringComparison.Ordinal) ||
                texto.StartsWith("b/", StringComparison.Ordinal))
            {
                texto = texto.Substring(2);
            }

            texto = texto.TrimStart('/');

            return texto.Length == 0 ? null : texto;
        }

        private static string NombreDeArchivo(string ruta)
        {
            int barra = ruta.LastIndexOf('/');
            return barra >= 0 ? ruta.Substring(barra + 1) : ruta;
        }
    }

    /// <summary>
    /// Interpreta un tramo de cabecera de hunk con formato "inicio" o "inicio,cuenta"
    /// y lo devuelve como el par de líneas (primera, última). Un tramo sin contador
    /// abarca una sola línea, que es la convención de git. Un contador de 0 (archivo
    /// recién creado o borrado) no aporta líneas comentables y se descarta.
    /// </summary>
    private static (int Inicio, int Fin)? InterpretarTramo(ReadOnlySpan<char> texto)
    {
        var i = 0;
        while (i < texto.Length && char.IsDigit(texto[i]))
        {
            i++;
        }

        if (i == 0 || !int.TryParse(texto.Slice(0, i), out var inicio))
        {
            return null;
        }

        var cuenta = 1;
        if (i < texto.Length && texto[i] == ',')
        {
            var inicioCuenta = i + 1;
            var fin = inicioCuenta;
            while (fin < texto.Length && char.IsDigit(texto[fin]))
            {
                fin++;
            }

            if (fin > inicioCuenta && !int.TryParse(texto.Slice(inicioCuenta, fin - inicioCuenta), out cuenta))
            {
                return null;
            }
        }

        if (cuenta <= 0)
        {
            return null;
        }

        // Una cabecera malformada podría desbordar el entero al sumar; acotamos.
        var ultima = cuenta - 1 > int.MaxValue - inicio ? int.MaxValue : inicio + cuenta - 1;
        return (inicio, ultima);
    }
}
