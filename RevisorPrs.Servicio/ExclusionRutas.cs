using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace RevisorPrs.Servicio;

/// <summary>
/// Decide qué archivos de un diff no se mandan a revisar.
///
/// Ficheros de bloqueo de dependencias, código generado, minificados y dependencias
/// vendorizadas entraban en el diff igual que el código escrito a mano. Son la mayor
/// fuente de ruido y de coste a la vez: hallazgos sobre código que nadie escribió,
/// pagados por token.
/// </summary>
/// <remarks>
/// Los patrones son globs, no expresiones regulares, porque quien configura esto
/// escribe rutas y no autómatas:
/// <list type="bullet">
///   <item><c>*</c> cubre cualquier cosa dentro de un segmento de ruta.</item>
///   <item><c>**</c> cubre cualquier número de segmentos.</item>
///   <item><c>?</c> cubre un carácter.</item>
///   <item>Un patrón SIN barras se compara contra el nombre del archivo, así que
///   <c>*.min.js</c> vale a cualquier profundidad sin escribir <c>**/</c>.</item>
/// </list>
/// La comparación no distingue mayúsculas: las rutas vienen de repositorios que pueden
/// vivir en sistemas de ficheros que tampoco distinguen.
/// </remarks>
public static class ExclusionRutas
{
    /// <summary>
    /// Patrones que se aplican cuando la configuración no dice otra cosa.
    ///
    /// La lista es deliberadamente conservadora: solo cosas que nadie revisa a mano.
    /// Las migraciones de base de datos NO están, aunque sean generadas, porque
    /// esconderlas taparía problemas reales.
    /// </summary>
    public static readonly string[] PorDefecto =
    {
        // Ficheros de bloqueo de dependencias.
        "package-lock.json",
        "npm-shrinkwrap.json",
        "yarn.lock",
        "pnpm-lock.yaml",
        "composer.lock",
        "Gemfile.lock",
        "poetry.lock",
        "Cargo.lock",
        "packages.lock.json",

        // Dependencias y salidas de compilación.
        "**/node_modules/**",
        "**/vendor/**",
        "**/dist/**",
        "**/bin/**",
        "**/obj/**",

        // Minificados y mapas de origen.
        "*.min.js",
        "*.min.css",
        "*.map",

        // Código generado.
        "*.designer.cs",
        "*.g.cs",
        "*.g.i.cs",
        "*.generated.cs",
        "*.pb.go",
        "*_pb2.py",

        // Instantáneas de pruebas.
        "*.snap",
    };

    private static readonly Dictionary<string, Regex> Cache = new(StringComparer.Ordinal);
    private static readonly object Candado = new();

    /// <summary>
    /// Indica si la ruta de un archivo del diff encaja con alguno de los patrones.
    /// Sin patrones, no se excluye nada.
    /// </summary>
    public static bool EstaExcluida(string? ruta, IReadOnlyList<string>? patrones)
    {
        if (patrones is null || patrones.Count == 0 || string.IsNullOrWhiteSpace(ruta))
        {
            return false;
        }

        string normalizada = ruta.Trim().Replace('\\', '/').TrimStart('/');
        string nombre = normalizada;
        int ultimaBarra = normalizada.LastIndexOf('/');
        if (ultimaBarra >= 0)
        {
            nombre = normalizada.Substring(ultimaBarra + 1);
        }

        foreach (string patron in patrones)
        {
            if (string.IsNullOrWhiteSpace(patron))
            {
                continue;
            }

            string limpio = patron.Trim().Replace('\\', '/');

            // Un patrón sin barras habla del nombre del archivo, no de su ubicación.
            string candidato = limpio.Contains('/') ? normalizada : nombre;

            if (ARegex(limpio).IsMatch(candidato))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Traduce un glob a la expresión regular equivalente, guardándola para no
    /// recompilarla en cada archivo de cada vuelta.
    /// </summary>
    private static Regex ARegex(string patron)
    {
        lock (Candado)
        {
            if (Cache.TryGetValue(patron, out Regex? guardada))
            {
                return guardada;
            }

            var expresion = new StringBuilder("^");
            for (int i = 0; i < patron.Length; i++)
            {
                char c = patron[i];
                if (c == '*')
                {
                    bool dobleAsterisco = i + 1 < patron.Length && patron[i + 1] == '*';
                    if (dobleAsterisco)
                    {
                        i++;
                        // "**/" cubre también el caso de cero segmentos, para que
                        // "**/dist/**" atrape "dist/app.js" en la raíz.
                        if (i + 1 < patron.Length && patron[i + 1] == '/')
                        {
                            i++;
                            expresion.Append("(?:.*/)?");
                        }
                        else
                        {
                            expresion.Append(".*");
                        }
                    }
                    else
                    {
                        // Un solo asterisco no cruza separadores de ruta.
                        expresion.Append("[^/]*");
                    }
                }
                else if (c == '?')
                {
                    expresion.Append("[^/]");
                }
                else
                {
                    expresion.Append(Regex.Escape(c.ToString()));
                }
            }
            expresion.Append('$');

            var compilada = new Regex(
                expresion.ToString(),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            Cache[patron] = compilada;
            return compilada;
        }
    }
}
