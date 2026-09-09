using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RevisorPrs.Servicio;

/// <summary>
/// Compone el cuerpo del comentario que se publica en el pull request.
///
/// Vive fuera de <see cref="ClienteBitbucket"/> a propósito: qué lee el equipo es una
/// decisión de producto, no de transporte, y separarla permite probar el texto sin
/// levantar HTTP.
/// </summary>
/// <remarks>
/// Antes se publicaba únicamente <see cref="Hallazgo.Resumen"/>. El prompt pide al
/// modelo un <see cref="Hallazgo.Detalle"/> con la justificación y, si procede, una
/// sugerencia concreta de arreglo: se pagaba por generarlo y se descartaba antes de
/// llegar a nadie. La severidad corría la misma suerte.
///
/// Dos decisiones sobre el formato:
/// - Se usan etiquetas de texto en negrita, no emoji. Renderizan igual en cualquier
///   cliente y no dan al equipo un motivo estético para pedir que se apague.
/// - No se añade firma al pie. Bitbucket ya muestra el autor del comentario, así que
///   repetir "revisión automática" en cada uno sería ruido en un hilo largo.
/// </remarks>
public static class FormateadorComentario
{
    /// <summary>
    /// Topes de longitud del texto que genera el modelo. Un resumen o un detalle sin
    /// límite convierten el comentario en un muro, y ese texto lo escribe el modelo
    /// leyendo un diff que controla quien abre el pull request.
    /// </summary>
    private const int TopeResumen = 300;
    private const int TopeDetalle = 2000;

    /// <summary>
    /// Compone el cuerpo en Markdown de un comentario.
    /// </summary>
    /// <param name="hallazgo">Hallazgo a comunicar.</param>
    /// <param name="anclado">
    /// <c>true</c> si el comentario va anclado a una línea concreta. En ese caso
    /// Bitbucket ya muestra el archivo y la línea junto al comentario, así que no se
    /// repiten en el texto. Cuando es <c>false</c> el comentario es general y la
    /// ubicación tiene que ir dentro.
    /// </param>
    public static string Componer(Hallazgo hallazgo, bool anclado)
    {
        ArgumentNullException.ThrowIfNull(hallazgo);

        var cabecera = new StringBuilder();
        cabecera.Append("**").Append(Severidades.Etiqueta(hallazgo.Severidad)).Append("**");

        if (!anclado)
        {
            string ubicacion = ComponerUbicacion(hallazgo);
            if (ubicacion.Length > 0)
            {
                cabecera.Append(" · `").Append(ubicacion).Append('`');
            }
        }

        string resumen = Acotar(hallazgo.Resumen, TopeResumen);
        if (resumen.Length > 0)
        {
            cabecera.Append(" · ").Append(resumen);
        }

        var cuerpo = new StringBuilder(cabecera.ToString());

        // El detalle solo aporta si dice algo más que el resumen: cuando el modelo
        // repite la misma frase, añadirla dos veces es ruido.
        string detalle = Acotar(hallazgo.Detalle, TopeDetalle);
        if (detalle.Length > 0 && !detalle.Equals(resumen, StringComparison.OrdinalIgnoreCase))
        {
            cuerpo.Append("\n\n").Append(detalle);
        }

        return cuerpo.ToString();
    }

    /// <summary>
    /// Compone el comentario de resumen: un solo comentario que recoge los hallazgos
    /// que NO se anclaron a una línea.
    /// </summary>
    /// <param name="enResumen">Hallazgos que se listan aquí. No puede estar vacía.</param>
    /// <param name="anclados">Cuántos hallazgos fueron a comentarios anclados.</param>
    /// <remarks>
    /// Existe para que un pull request con doce hallazgos no genere doce notificaciones
    /// a cada persona suscrita, que es la forma más rápida de que un equipo silencie un
    /// revisor automático. Los graves siguen anclados a su línea, porque ahí es donde
    /// sirven; el resto se agrupa aquí.
    ///
    /// El detalle de cada hallazgo se conserva: es lo accionable, y perderlo por
    /// agrupar dejaría el resumen en una lista de quejas sin sugerencia de arreglo.
    /// </remarks>
    public static string ComponerResumen(IReadOnlyList<Hallazgo> enResumen, int anclados)
    {
        ArgumentNullException.ThrowIfNull(enResumen);
        if (enResumen.Count == 0)
        {
            throw new ArgumentException(
                "El resumen necesita al menos un hallazgo que listar.", nameof(enResumen));
        }

        var texto = new StringBuilder();
        texto.Append("**Revisión automática** · ").Append(ContarPorSeveridad(enResumen));

        if (anclados > 0)
        {
            texto.Append("\n\n")
                .Append(anclados == 1
                    ? "Hay además 1 hallazgo comentado en su línea."
                    : $"Hay además {anclados.ToString(CultureInfo.InvariantCulture)} hallazgos comentados en sus líneas.");
        }

        foreach (var hallazgo in enResumen)
        {
            texto.Append("\n\n---\n\n").Append(Componer(hallazgo, anclado: false));
        }

        return texto.ToString();
    }

    /// <summary>
    /// "2 errores, 6 avisos y 4 notas", omitiendo las severidades sin hallazgos.
    /// </summary>
    private static string ContarPorSeveridad(IReadOnlyList<Hallazgo> hallazgos)
    {
        // El peso se guarda al contar, tomado de la severidad original. Derivarlo
        // después de la etiqueta no funciona: "Nota" es un texto para mostrar, no una
        // severidad, y no está en la tabla de pesos.
        var cuentas = new Dictionary<string, (int Peso, int Cuenta)>(StringComparer.Ordinal);
        var orden = new List<string>();

        foreach (var hallazgo in hallazgos)
        {
            string etiqueta = Severidades.Etiqueta(hallazgo.Severidad);
            if (cuentas.TryGetValue(etiqueta, out var actual))
            {
                cuentas[etiqueta] = (actual.Peso, actual.Cuenta + 1);
            }
            else
            {
                cuentas[etiqueta] = (Severidades.Peso(hallazgo.Severidad) ?? -1, 1);
                orden.Add(etiqueta);
            }
        }

        // De más grave a menos, dejando al final las severidades que no reconocemos.
        orden.Sort((a, b) => cuentas[b].Peso.CompareTo(cuentas[a].Peso));

        var partes = new List<string>(orden.Count);
        foreach (var etiqueta in orden)
        {
            int cuenta = cuentas[etiqueta].Cuenta;
            partes.Add(cuenta.ToString(CultureInfo.InvariantCulture) + " " + Plural(etiqueta, cuenta));
        }

        if (partes.Count == 1)
        {
            return partes[0];
        }

        return string.Join(", ", partes.GetRange(0, partes.Count - 1)) + " y " + partes[^1];
    }

    private static string Plural(string etiqueta, int cuenta)
    {
        string minuscula = etiqueta.ToLowerInvariant();
        if (cuenta == 1)
        {
            return minuscula;
        }

        // "error" → "errores"; "aviso" → "avisos"; "nota" → "notas".
        return minuscula.EndsWith("r", StringComparison.Ordinal)
            ? minuscula + "es"
            : minuscula + "s";
    }

    /// <summary>Recorta el texto del modelo al tope indicado.</summary>
    private static string Acotar(string? texto, int tope)
    {
        string limpio = (texto ?? string.Empty).Trim();
        return limpio.Length <= tope ? limpio : limpio.Substring(0, tope) + "…";
    }

    private static string ComponerUbicacion(Hallazgo hallazgo)
    {
        string archivo = (hallazgo.Archivo ?? string.Empty).Trim();
        if (archivo.Length == 0)
        {
            return string.Empty;
        }

        return hallazgo.Linea.HasValue
            ? archivo + ":" + hallazgo.Linea.Value.ToString(CultureInfo.InvariantCulture)
            : archivo;
    }
}
