using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RevisorPrs.Servicio;

/// <summary>
/// Representa un hallazgo encontrado durante la revisión de un pull request.
/// </summary>
/// <param name="Archivo">Ruta del archivo donde se encontró el hallazgo.</param>
/// <param name="Linea">Número de línea donde se encontró el hallazgo (puede ser null).</param>
/// <param name="Severidad">Nivel de severidad (ej: "error", "warning", "info").</param>
/// <param name="Resumen">Descripción breve del hallazgo.</param>
/// <param name="Detalle">Descripción detallada del hallazgo.</param>
public record Hallazgo(string Archivo, int? Linea, string Severidad, string Resumen, string Detalle)
{
    /// <summary>
    /// Identidad estable del hallazgo, usada para no publicar dos veces el mismo
    /// comentario en un pull request.
    /// </summary>
    /// <remarks>
    /// La huella se calcula SOLO con archivo, línea y severidad. Deja fuera a propósito
    /// el <see cref="Resumen"/> y el <see cref="Detalle"/>, que son prosa del modelo:
    /// al reintentar tras una caída se vuelve a llamar al LLM sobre el mismo diff, y
    /// basta con que reformule una frase para que una huella basada en el texto deje de
    /// coincidir justo en el caso que se pretende cubrir.
    ///
    /// El precio es que dos hallazgos distintos sobre la misma línea y con la misma
    /// severidad se consideran el mismo. Es un intercambio deliberado: en un revisor
    /// automático, callar de más molesta mucho menos que repetirse.
    /// </remarks>
    public string Huella()
    {
        string archivo = (Archivo ?? string.Empty).Trim();
        string linea = Linea.HasValue
            ? Linea.Value.ToString(CultureInfo.InvariantCulture)
            : "-";
        string severidad = (Severidad ?? string.Empty).Trim().ToLowerInvariant();

        byte[] bytes = Encoding.UTF8.GetBytes($"{archivo}\n{linea}\n{severidad}");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}