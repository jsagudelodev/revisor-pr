using System;
using System.Globalization;
using System.Text;

namespace RevisorPrs.Servicio;

/// <summary>
/// Antepone a cada línea del diff su número de línea, para que el modelo no tenga que
/// deducirlo.
/// </summary>
/// <remarks>
/// El prompt le pedía al modelo contar líneas a partir de las cabeceras <c>@@</c>, que es
/// justo lo que los modelos hacen mal: hay que llevar dos contadores en paralelo (archivo
/// viejo y nuevo) y avanzarlos de forma distinta según la marca de cada línea. De ahí
/// venía buena parte del ruido que <see cref="FiltroRuido"/> tiene que descartar después.
/// Contar es trabajo determinista, así que lo hacemos nosotros.
///
/// Las líneas de cabecera (<c>diff --git</c>, <c>---</c>, <c>+++</c>, <c>@@</c>, modos,
/// índices) pasan intactas: el modelo las necesita para saber de qué archivo habla, y
/// numerarlas no significaría nada.
/// </remarks>
public static class NumeradorDiff
{
    /// <summary>
    /// Ancho de la columna del número. Con cinco caracteres caben ficheros de hasta
    /// 99999 líneas sin descuadrar la lectura.
    /// </summary>
    private const int AnchoNumero = 5;

    /// <summary>
    /// Devuelve el diff con cada línea de contenido precedida por su número.
    /// </summary>
    /// <remarks>
    /// El número es el del archivo NUEVO para las líneas de contexto y las añadidas, y
    /// el del archivo ORIGINAL para las eliminadas. Es la misma convención que usa
    /// <see cref="FiltroRuido"/> al validar, así que lo que el modelo señala y lo que
    /// nosotros aceptamos hablan del mismo sistema de numeración.
    /// </remarks>
    public static string Numerar(string diff)
    {
        ArgumentNullException.ThrowIfNull(diff);

        if (diff.Length == 0)
        {
            return diff;
        }

        var salida = new StringBuilder(diff.Length + diff.Length / 8);
        var lineas = diff.Split('\n');

        int lineaVieja = 0;
        int lineaNueva = 0;
        bool dentroDeHunk = false;

        for (int i = 0; i < lineas.Length; i++)
        {
            string linea = lineas[i];

            if (EsCabecera(linea, out bool abreHunk))
            {
                if (abreHunk)
                {
                    (lineaVieja, lineaNueva) = LeerInicios(linea);
                    dentroDeHunk = true;
                }
                else if (linea.StartsWith("diff --git ", StringComparison.Ordinal))
                {
                    // Empieza otro archivo: hasta su primer @@ no hay nada que numerar.
                    dentroDeHunk = false;
                }

                salida.Append(linea);
            }
            else if (!dentroDeHunk || linea.Length == 0)
            {
                // Fuera de un hunk no sabemos a qué línea corresponde nada.
                salida.Append(linea);
            }
            else
            {
                char marca = linea[0];
                switch (marca)
                {
                    case '+':
                        salida.Append(Prefijo(lineaNueva)).Append(linea);
                        lineaNueva++;
                        break;

                    case '-':
                        salida.Append(Prefijo(lineaVieja)).Append(linea);
                        lineaVieja++;
                        break;

                    case ' ':
                        salida.Append(Prefijo(lineaNueva)).Append(linea);
                        lineaVieja++;
                        lineaNueva++;
                        break;

                    default:
                        // "\ No newline at end of file" y cualquier cosa inesperada.
                        salida.Append(linea);
                        break;
                }
            }

            if (i < lineas.Length - 1)
            {
                salida.Append('\n');
            }
        }

        return salida.ToString();
    }

    private static string Prefijo(int numero) =>
        numero.ToString(CultureInfo.InvariantCulture).PadLeft(AnchoNumero) + " ";

    private static bool EsCabecera(string linea, out bool abreHunk)
    {
        abreHunk = linea.StartsWith("@@", StringComparison.Ordinal);
        if (abreHunk)
        {
            return true;
        }

        // "---" y "+++" empiezan por las mismas marcas que el contenido, así que van
        // antes de la comprobación de marca, no después.
        return linea.StartsWith("diff --git ", StringComparison.Ordinal)
            || linea.StartsWith("--- ", StringComparison.Ordinal)
            || linea.StartsWith("+++ ", StringComparison.Ordinal)
            || linea.StartsWith("index ", StringComparison.Ordinal)
            || linea.StartsWith("old mode ", StringComparison.Ordinal)
            || linea.StartsWith("new mode ", StringComparison.Ordinal)
            || linea.StartsWith("new file mode ", StringComparison.Ordinal)
            || linea.StartsWith("deleted file mode ", StringComparison.Ordinal)
            || linea.StartsWith("similarity index ", StringComparison.Ordinal)
            || linea.StartsWith("rename from ", StringComparison.Ordinal)
            || linea.StartsWith("rename to ", StringComparison.Ordinal)
            || linea.StartsWith("Binary files ", StringComparison.Ordinal);
    }

    /// <summary>
    /// Lee los números de inicio de "@@ -viejo[,n] +nuevo[,n] @@".
    /// </summary>
    private static (int Viejo, int Nuevo) LeerInicios(string cabecera)
    {
        int viejo = 0;
        int nuevo = 0;

        foreach (var parte in cabecera.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (parte.Length <= 1)
            {
                continue;
            }

            if (parte[0] == '-')
            {
                viejo = LeerEntero(parte.AsSpan(1));
            }
            else if (parte[0] == '+')
            {
                nuevo = LeerEntero(parte.AsSpan(1));
            }
        }

        return (viejo, nuevo);
    }

    private static int LeerEntero(ReadOnlySpan<char> texto)
    {
        int i = 0;
        while (i < texto.Length && char.IsDigit(texto[i]))
        {
            i++;
        }

        return i > 0 && int.TryParse(texto.Slice(0, i), out int valor) ? valor : 0;
    }
}
