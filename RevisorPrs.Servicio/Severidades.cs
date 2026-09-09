using System;
using System.Collections.Generic;

namespace RevisorPrs.Servicio;

/// <summary>
/// Vocabulario de severidad compartido por todo el servicio.
///
/// Existen DOS vocabularios equivalentes y ambos son legítimos:
/// <see cref="PromptRevision"/> pide al modelo "error", "warning" o "info", mientras
/// que el umbral se configura en español con "baja", "media" y "alta". Vive aquí para
/// que el filtro de ruido y el formateador de comentarios no mantengan cada uno su
/// propia tabla y acaben discrepando.
/// </summary>
public static class Severidades
{
    /// <summary>
    /// Peso ordinal de cada severidad reconocida, de menor a mayor.
    /// </summary>
    private static readonly Dictionary<string, int> Pesos = new(StringComparer.OrdinalIgnoreCase)
    {
        // Vocabulario que produce el modelo.
        ["info"] = 0,
        ["warning"] = 1,
        ["error"] = 2,

        // Vocabulario con el que se configura el umbral.
        ["baja"] = 0,
        ["media"] = 1,
        ["alta"] = 2,
    };

    /// <summary>
    /// Etiqueta que se muestra al equipo en el comentario del pull request.
    /// </summary>
    private static readonly string[] Etiquetas = { "Nota", "Aviso", "Error" };

    /// <summary>
    /// Peso de una severidad, o <c>null</c> si no se reconoce.
    /// </summary>
    public static int? Peso(string? severidad)
    {
        if (string.IsNullOrWhiteSpace(severidad))
        {
            return null;
        }

        return Pesos.TryGetValue(severidad.Trim(), out int peso) ? peso : null;
    }

    /// <summary>
    /// Etiqueta legible de una severidad. Lo que no se reconoce pasa a "Nota".
    /// </summary>
    /// <remarks>
    /// Antes se devolvía el texto del modelo tal cual, para no perder información. Se
    /// cambió a propósito: la etiqueta se publica en negrita dentro de un comentario
    /// Markdown del pull request, y ese texto lo controla el modelo, que a su vez lee un
    /// diff que controla quien abre el PR. Devolver texto arbitrario ahí es dejar que el
    /// contenido del pull request escriba en la cabecera del comentario.
    /// </remarks>
    public static string Etiqueta(string? severidad)
    {
        int? peso = Peso(severidad);
        return peso.HasValue ? Etiquetas[peso.Value] : "Nota";
    }
}
