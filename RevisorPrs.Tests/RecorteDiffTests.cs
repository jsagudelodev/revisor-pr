using System.Text;
using RevisorPrs.Servicio;
using Xunit;

namespace RevisorPrs.Tests;

public class RecorteDiffTests
{
    private const string MarcaArchivo = "diff --git ";

    private static string ConstruirSeccion(string ruta, int lineasContenido)
    {
        var sb = new StringBuilder();
        sb.Append(MarcaArchivo);
        sb.Append("a/");
        sb.Append(ruta);
        sb.Append(" b/");
        sb.Append(ruta);
        sb.Append('\n');
        sb.Append("index 0000000..1111111 100644\n");
        sb.Append("--- a/");
        sb.Append(ruta);
        sb.Append('\n');
        sb.Append("+++ b/");
        sb.Append(ruta);
        sb.Append('\n');
        sb.Append("@@ -1,").Append(lineasContenido).Append(" +1,").Append(lineasContenido).Append(" @@\n");
        for (int i = 0; i < lineasContenido; i++)
        {
            sb.Append("+linea ").Append(i).Append(" de ").Append(ruta).Append('\n');
        }
        return sb.ToString();
    }

    [Fact]
    public void Recortar_ConDiffPequeno_LoDevuelveIntacto()
    {
        var config = new ConfiguracionBitbucket { TopeBytesDiff = 100_000 };
        var recortador = new RecortadorDiff(config);

        var diff = ConstruirSeccion("src/uno.cs", 5)
            + ConstruirSeccion("src/dos.cs", 5);

        var resultado = recortador.Recortar(diff);

        Assert.Equal(diff, resultado);
        Assert.DoesNotContain("[RecortadorDiff]", resultado);
    }

    [Fact]
    public void Recortar_ConDiffGrande_IncluyeArchivosEnterosYNombraLosOmitidos()
    {
        // Tope pequeño: el primer archivo cabe, el segundo y el tercero no.
        var config = new ConfiguracionBitbucket { TopeBytesDiff = 400 };
        var recortador = new RecortadorDiff(config);

        var diff = ConstruirSeccion("src/primero.cs", 3)
            + ConstruirSeccion("src/segundo.cs", 20)
            + ConstruirSeccion("src/tercero.cs", 20);

        var resultado = recortador.Recortar(diff);

        // El primer archivo debe estar entero, sin cortes a mitad.
        Assert.Contains(MarcaArchivo + "a/src/primero.cs", resultado);
        Assert.Contains("+linea 2 de src/primero.cs", resultado);

        // Los archivos omitidos deben nombrarse en la nota final.
        Assert.Contains("[RecortadorDiff]", resultado);
        Assert.Contains("src/segundo.cs", resultado);
        Assert.Contains("src/tercero.cs", resultado);
        Assert.Contains("2 archivo(s)", resultado);

        // El resultado completo no debe superar el tope + la nota de omitidos.
        Assert.True(
            Encoding.UTF8.GetByteCount(resultado) <= config.TopeBytesDiff + 200,
            "El resultado no debería crecer mucho más allá del tope.");
    }

    [Fact]
    public void Recortar_ConUnSoloArchivoEnorme_NoDevuelveMedioArchivo()
    {
        // Comportamiento decidido: si un único archivo ya supera el tope, se incluye
        // ENTERO igualmente (no devolvemos un resultado vacío ni un archivo a medias).
        // La nota de omitidos NO se añade en este caso, porque no hay nada que omitir:
        // siempre devolvemos al menos un archivo, por grande que sea.
        var config = new ConfiguracionBitbucket { TopeBytesDiff = 50 };
        var recortador = new RecortadorDiff(config);

        var diff = ConstruirSeccion("src/enorme.cs", 50);

        var resultado = recortador.Recortar(diff);

        // Se devuelve el archivo entero, sin cortes a mitad de línea.
        Assert.Contains(MarcaArchivo + "a/src/enorme.cs", resultado);
        Assert.Contains("+linea 49 de src/enorme.cs", resultado);

        // Como no se omitió nada, no añadimos la nota final.
        Assert.DoesNotContain("[RecortadorDiff]", resultado);
    }
}
