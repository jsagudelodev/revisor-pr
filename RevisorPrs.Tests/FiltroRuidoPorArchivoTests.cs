using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;
using RevisorPrs.Servicio;
using Xunit;

namespace RevisorPrs.Tests;

/// <summary>
/// Pruebas de que la línea de un hallazgo se comprueba contra los hunks DE SU ARCHIVO (A5).
///
/// Antes los tramos se acumulaban en una lista única para todo el diff. Como los hunks
/// suelen empezar en números bajos y solaparse entre archivos, un hallazgo en
/// <c>B.cs:11</c> pasaba el filtro porque <c>A.cs</c> tenía un hunk que cubría la 11:
/// en un pull request de varios ficheros la regla no descartaba casi nada.
/// </summary>
public class FiltroRuidoPorArchivoTests
{
    /// <summary>Sin umbral de severidad, para aislar la regla de la línea.</summary>
    private static FiltroRuido CrearFiltro() =>
        new(Options.Create(new ConfiguracionLlm()), new RegistradorFalso<FiltroRuido>());

    private static string Seccion(string ruta, int inicio, int cuenta) =>
        $"diff --git a/{ruta} b/{ruta}\n" +
        $"--- a/{ruta}\n" +
        $"+++ b/{ruta}\n" +
        $"@@ -{inicio},{cuenta} +{inicio},{cuenta} @@\n" +
        "+cambio\n";

    private static Hallazgo En(string archivo, int linea) =>
        new(archivo, linea, "error", $"hallazgo en {archivo}:{linea}", "detalle");

    [Fact]
    public void LineaQueSoloExisteEnOtroArchivo_SeDescarta()
    {
        // A.cs cambia las líneas 10-12; B.cs cambia las 100-102.
        string diff = Seccion("src/A.cs", 10, 3) + Seccion("src/B.cs", 100, 3);

        var resultado = CrearFiltro().Filtrar(
            new List<Hallazgo>
            {
                En("src/A.cs", 11),   // dentro de SU archivo
                En("src/B.cs", 11),   // la 11 es de A.cs, no de B.cs
                En("src/B.cs", 101),  // dentro de SU archivo
                En("src/A.cs", 101),  // la 101 es de B.cs, no de A.cs
            },
            diff);

        Assert.Equal(
            new[] { "src/A.cs:11", "src/B.cs:101" },
            resultado.Select(h => $"{h.Archivo}:{h.Linea}").ToArray());
    }

    [Fact]
    public void ArchivoQueElPullRequestNoToca_SeDescarta()
    {
        string diff = Seccion("src/A.cs", 10, 3);

        var resultado = CrearFiltro().Filtrar(
            new List<Hallazgo> { En("src/Ausente.cs", 11) },
            diff);

        Assert.Empty(resultado);
    }

    [Theory]
    // El modelo puede citar la ruta con los prefijos que ve en el diff, o sin ellos.
    [InlineData("src/A.cs")]
    [InlineData("a/src/A.cs")]
    [InlineData("b/src/A.cs")]
    [InlineData("/src/A.cs")]
    [InlineData("src\\A.cs")]
    // O solo el nombre, si no hay ambigüedad.
    [InlineData("A.cs")]
    public void LaRutaSeReconoceAunqueElModeloLaEscribaDeOtraForma(string comoLaCita)
    {
        string diff = Seccion("src/A.cs", 10, 3);

        var resultado = CrearFiltro().Filtrar(new List<Hallazgo> { En(comoLaCita, 11) }, diff);

        Assert.Single(resultado);
    }

    [Fact]
    public void DosArchivosConElMismoNombre_NoSeAdivina()
    {
        // "Cliente.cs" existe en dos carpetas con hunks distintos: resolver por nombre
        // sería adivinar, así que se descarta.
        string diff = Seccion("api/Cliente.cs", 10, 3) + Seccion("web/Cliente.cs", 500, 3);

        var resultado = CrearFiltro().Filtrar(new List<Hallazgo> { En("Cliente.cs", 11) }, diff);

        Assert.Empty(resultado);

        // Con la ruta completa sí se resuelve.
        Assert.Single(CrearFiltro().Filtrar(new List<Hallazgo> { En("api/Cliente.cs", 11) }, diff));
    }

    [Fact]
    public void ConRenombrado_SeAceptanLaRutaViejaYLaNueva()
    {
        // El tramo "-" habla del archivo viejo y el "+" del nuevo.
        string diff =
            "diff --git a/src/Viejo.cs b/src/Nuevo.cs\n" +
            "--- a/src/Viejo.cs\n" +
            "+++ b/src/Nuevo.cs\n" +
            "@@ -10,3 +20,3 @@\n" +
            "+cambio\n";

        var filtro = CrearFiltro();

        Assert.Single(filtro.Filtrar(new List<Hallazgo> { En("src/Nuevo.cs", 21) }, diff));
        Assert.Single(filtro.Filtrar(new List<Hallazgo> { En("src/Viejo.cs", 11) }, diff));

        // Pero la línea del nuevo no vale para el viejo.
        Assert.Empty(filtro.Filtrar(new List<Hallazgo> { En("src/Viejo.cs", 21) }, diff));
    }

    [Fact]
    public void ArchivoRecienCreado_SusLineasSeAceptan()
    {
        // Git escribe "--- /dev/null" para un archivo nuevo: no hay ruta vieja, pero la
        // nueva sí tiene que reconocerse.
        string diff =
            "diff --git a/src/Nuevo.cs b/src/Nuevo.cs\n" +
            "new file mode 100644\n" +
            "--- /dev/null\n" +
            "+++ b/src/Nuevo.cs\n" +
            "@@ -0,0 +1,5 @@\n" +
            "+cambio\n";

        Assert.Single(CrearFiltro().Filtrar(new List<Hallazgo> { En("src/Nuevo.cs", 3) }, diff));
    }

    [Fact]
    public void DiffSinCabecerasDeArchivo_NoDescartaNada()
    {
        // Un diff que no sabemos atribuir es un diff que no entendemos: preferimos
        // conservar los hallazgos antes que tirarlos todos por un fallo de lectura.
        string diff = "@@ -10,3 +10,3 @@\n+cambio\n";

        Assert.Single(CrearFiltro().Filtrar(new List<Hallazgo> { En("cualquiera.cs", 11) }, diff));
    }

    [Fact]
    public void SinLinea_SeConservaSiElArchivoSiEstaEnElDiff()
    {
        // Un hallazgo sin línea es un comentario general sobre el archivo, y eso sigue
        // valiendo mientras el pull request toque ese archivo.
        string diff = Seccion("src/A.cs", 10, 3);
        var general = new Hallazgo("src/A.cs", null, "error", "comentario general", "detalle");

        Assert.Single(CrearFiltro().Filtrar(new List<Hallazgo> { general }, diff));
    }

    [Fact]
    public void SinLinea_SobreUnArchivoQueElPrNoToca_SeDescarta()
    {
        // Este test afirmaba lo contrario hasta B4. Se cambió a propósito: un hallazgo
        // sobre un archivo ajeno al pull request no solo es ruido, es la señal de que el
        // modelo se fue del guion, que es el resultado de una inyección lograda.
        string diff = Seccion("src/A.cs", 10, 3);
        var general = new Hallazgo("src/Ausente.cs", null, "error", "comentario general", "detalle");

        Assert.Empty(CrearFiltro().Filtrar(new List<Hallazgo> { general }, diff));
    }
}
