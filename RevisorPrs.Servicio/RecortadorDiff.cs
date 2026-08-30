using System;
using System.Collections.Generic;
using System.Text;

namespace RevisorPrs.Servicio;

/// <summary>
/// Recorta un diff de Bitbucket para que no supere un tope de bytes.
/// Lo hace por archivo completo (las secciones empiezan por "diff --git"):
/// nunca deja un archivo cortado a mitad. Si algo queda fuera, lo nombra al final.
/// Si el diff cabe entero, se devuelve intacto, sin añadir nada.
/// </summary>
public class RecortadorDiff
{
    private const string MarcaInicioArchivo = "diff --git ";
    private const string ResumenOmitidos =
        "\n\n[RecortadorDiff] Se omitieron {0} archivo(s) por superar el tope de bytes: {1}";

    /// <summary>
    /// Recorta el diff. Si el diff ya cabe en el tope, se devuelve intacto.
    /// Si no, se incluyen archivos enteros hasta llegar al tope y se listan los omitidos.
    /// Si un único archivo ya supera el tope, se incluye entero igualmente
    /// (sin él el recorte devolvería un resultado vacío y se perdería toda la información);
    /// en ese caso no se omite nada y la nota de omitidos no se añade.
    /// </summary>
    public string Recortar(string diff)
    {
        if (diff is null)
        {
            throw new ArgumentNullException(nameof(diff));
        }

        var topeBytes = _configuracion.TopeBytesDiff;
        if (topeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(_configuracion.TopeBytesDiff),
                "El tope de bytes del diff debe ser positivo.");
        }

        var bytes = Encoding.UTF8.GetByteCount(diff);
        if (bytes <= topeBytes)
        {
            return diff;
        }

        // Partimos el diff en secciones por archivo.
        // La primera sección (si existe) puede no empezar por "diff --git": suele ser
        // una cabecera global (p. ej. "Subproject commit ..."); la respetamos como preludio.
        var secciones = PartirPorArchivo(diff);
        if (secciones.Count == 0)
        {
            return diff;
        }

        var primerArchivo = secciones[0].EmpiezaConMarca;
        var preludio = primerArchivo ? string.Empty : secciones[0].Contenido;
        var archivos = primerArchivo ? secciones : secciones.GetRange(1, secciones.Count - 1);

        var preludioBytes = Encoding.UTF8.GetByteCount(preludio);

        // Si ni siquiera el preludio cabe solo, sigo incluyendo archivos igual:
        // siempre devolvemos al menos el primer archivo, por grande que sea.
        var incluidos = new List<string>();
        var omitidos = new List<string>();
        long consumo = preludioBytes;
        bool forzarPrimerArchivo = true;

        foreach (var seccion in archivos)
        {
            var nombre = ExtraerNombreArchivo(seccion.Contenido);
            var bytesSeccion = Encoding.UTF8.GetByteCount(seccion.Contenido);

            if (consumo + bytesSeccion <= topeBytes || forzarPrimerArchivo)
            {
                incluidos.Add(seccion.Contenido);
                consumo += bytesSeccion;
                forzarPrimerArchivo = false;
            }
            else
            {
                omitidos.Add(nombre);
            }
        }

        var sb = new StringBuilder(preludio);
        foreach (var s in incluidos)
        {
            sb.Append(s);
        }

        if (omitidos.Count > 0)
        {
            sb.AppendFormat(ResumenOmitidos, omitidos.Count, string.Join(", ", omitidos));
        }

        return sb.ToString();
    }

    private readonly ConfiguracionBitbucket _configuracion;

    public RecortadorDiff(ConfiguracionBitbucket configuracion)
    {
        _configuracion = configuracion
            ?? throw new ArgumentNullException(nameof(configuracion));
    }

    private static List<SeccionDif> PartirPorArchivo(string diff)
    {
        var lineas = diff.Split('\n');
        var secciones = new List<SeccionDif>();
        var actual = new StringBuilder();
        bool primera = true;

        for (int i = 0; i < lineas.Length; i++)
        {
            var linea = lineas[i];
            bool empiezaArchivo = linea.StartsWith(MarcaInicioArchivo, StringComparison.Ordinal);

            if (empiezaArchivo && !primera)
            {
                secciones.Add(new SeccionDif(actual.ToString(), empiezaConMarca: true));
                actual.Clear();
            }
            else if (empiezaArchivo)
            {
                primera = false;
            }

            actual.Append(linea);
            if (i < lineas.Length - 1)
            {
                actual.Append('\n');
            }
        }

        if (actual.Length > 0)
        {
            secciones.Add(new SeccionDif(actual.ToString(), empiezaConMarca: !primera && secciones.Count == 0
                ? true
                : (actual.ToString().StartsWith(MarcaInicioArchivo, StringComparison.Ordinal))));
        }

        // Si sólo hay una sección y no empieza por marca, la marcamos como no-archivo (preludio).
        if (secciones.Count == 1 && !secciones[0].Contenido.StartsWith(MarcaInicioArchivo, StringComparison.Ordinal))
        {
            secciones[0] = new SeccionDif(secciones[0].Contenido, empiezaConMarca: false);
        }

        return secciones;
    }

    private static string ExtraerNombreArchivo(string seccion)
    {
        // La cabecera "diff --git a/ruta b/ruta" trae la ruta dos veces; nos quedamos con la primera.
        var primeraLinea = seccion;
        var salto = primeraLinea.IndexOf('\n');
        if (salto >= 0)
        {
            primeraLinea = primeraLinea.Substring(0, salto);
        }

        // Quitar prefijo "diff --git " y separar por espacios: "a/ruta b/ruta"
        var resto = primeraLinea.StartsWith(MarcaInicioArchivo, StringComparison.Ordinal)
            ? primeraLinea.Substring(MarcaInicioArchivo.Length)
            : primeraLinea;

        var espacio = resto.IndexOf(' ');
        var ruta = espacio >= 0 ? resto.Substring(0, espacio) : resto;

        // Quitar prefijo "a/" o "b/" si está presente.
        if (ruta.StartsWith("a/", StringComparison.Ordinal) || ruta.StartsWith("b/", StringComparison.Ordinal))
        {
            ruta = ruta.Substring(2);
        }

        return string.IsNullOrEmpty(ruta) ? "(archivo sin nombre)" : ruta;
    }

    private readonly struct SeccionDif
    {
        public SeccionDif(string contenido, bool empiezaConMarca)
        {
            Contenido = contenido;
            EmpiezaConMarca = empiezaConMarca;
        }

        public string Contenido { get; }
        public bool EmpiezaConMarca { get; }
    }
}