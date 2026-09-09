using System;
using System.Linq;
using RevisorPrs.Servicio;
using Xunit;

namespace RevisorPrs.Tests;

/// <summary>
/// Pruebas de la numeración del diff que se envía al modelo (B1).
///
/// El prompt le pedía al modelo deducir el número de línea contando desde las cabeceras
/// <c>@@</c>, que exige llevar dos contadores en paralelo y avanzarlos de forma distinta
/// según la marca de cada línea. Es trabajo determinista y los modelos lo hacen mal, así
/// que ahora lo hacemos nosotros.
/// </summary>
public class NumeradorDiffTests
{
    /// <summary>
    /// Devuelve los pares (número, contenido) de las líneas que llevan número.
    /// </summary>
    private static (int Numero, string Contenido)[] Numeradas(string numerado)
    {
        var resultado = numerado
            .Split('\n')
            .Where(l => l.Length > 6 && int.TryParse(l.Substring(0, 5).Trim(), out _))
            .Select(l => (int.Parse(l.Substring(0, 5).Trim()), l.Substring(6)))
            .ToArray();
        return resultado;
    }

    [Fact]
    public void Numerar_ContextoYAnadidas_LlevanElNumeroDelArchivoNuevo()
    {
        string diff = string.Join("\n",
            "diff --git a/A.cs b/A.cs",
            "--- a/A.cs",
            "+++ b/A.cs",
            "@@ -10,2 +10,3 @@",
            "     contexto",
            "+    anadida",
            "     otra");

        var numeradas = Numeradas(NumeradorDiff.Numerar(diff));

        Assert.Equal(
            new[] { (10, "     contexto"), (11, "+    anadida"), (12, "     otra") },
            numeradas);
    }

    [Fact]
    public void Numerar_Eliminadas_LlevanElNumeroDelArchivoOriginal()
    {
        // Los dos contadores divergen: tras una línea eliminada, el nuevo no avanza.
        string diff = string.Join("\n",
            "diff --git a/A.cs b/A.cs",
            "--- a/A.cs",
            "+++ b/A.cs",
            "@@ -10,3 +10,2 @@",
            "     contexto",
            "-    eliminada",
            "     final");

        var numeradas = Numeradas(NumeradorDiff.Numerar(diff));

        Assert.Equal(
            new[]
            {
                (10, "     contexto"),  // nueva 10
                (11, "-    eliminada"), // vieja 11
                (11, "     final"),     // nueva 11
            },
            numeradas);
    }

    [Fact]
    public void Numerar_VariosHunks_ReiniciaLosContadoresEnCadaCabecera()
    {
        string diff = string.Join("\n",
            "diff --git a/A.cs b/A.cs",
            "--- a/A.cs",
            "+++ b/A.cs",
            "@@ -10,1 +10,1 @@",
            "     primera",
            "@@ -40,1 +41,1 @@",
            "     segunda");

        var numeradas = Numeradas(NumeradorDiff.Numerar(diff));

        Assert.Equal(new[] { (10, "     primera"), (41, "     segunda") }, numeradas);
    }

    [Fact]
    public void Numerar_VariosArchivos_CadaUnoEmpiezaEnSuPropiaCabecera()
    {
        string diff = string.Join("\n",
            "diff --git a/A.cs b/A.cs",
            "--- a/A.cs",
            "+++ b/A.cs",
            "@@ -1,1 +1,1 @@",
            "+    de A",
            "diff --git a/B.cs b/B.cs",
            "--- a/B.cs",
            "+++ b/B.cs",
            "@@ -100,1 +100,1 @@",
            "+    de B");

        var numeradas = Numeradas(NumeradorDiff.Numerar(diff));

        Assert.Equal(new[] { (1, "+    de A"), (100, "+    de B") }, numeradas);
    }

    [Theory]
    [InlineData("diff --git a/A.cs b/A.cs")]
    [InlineData("--- a/A.cs")]
    [InlineData("+++ b/A.cs")]
    [InlineData("index 1a2b3c4..5d6e7f8 100644")]
    [InlineData("new file mode 100644")]
    [InlineData("deleted file mode 100644")]
    [InlineData("rename from src/Viejo.cs")]
    [InlineData("rename to src/Nuevo.cs")]
    [InlineData("Binary files a/logo.png and b/logo.png differ")]
    public void Numerar_LasCabecerasPasanIntactas(string cabecera)
    {
        // "---" y "+++" empiezan por las mismas marcas que el contenido: si se numeraran
        // se descuadrarían los contadores del archivo entero.
        string diff = string.Join("\n",
            "diff --git a/A.cs b/A.cs",
            cabecera,
            "@@ -5,1 +5,1 @@",
            "     contenido");

        string numerado = NumeradorDiff.Numerar(diff);

        Assert.Contains("\n" + cabecera + "\n", "\n" + numerado + "\n", StringComparison.Ordinal);
        Assert.Equal(new[] { (5, "     contenido") }, Numeradas(numerado));
    }

    [Fact]
    public void Numerar_LaMarcaDeFinSinSaltoNoSeNumera()
    {
        string diff = string.Join("\n",
            "diff --git a/A.cs b/A.cs",
            "--- a/A.cs",
            "+++ b/A.cs",
            "@@ -1,1 +1,1 @@",
            "+    ultima",
            "\\ No newline at end of file");

        string numerado = NumeradorDiff.Numerar(diff);

        Assert.Contains("\\ No newline at end of file", numerado, StringComparison.Ordinal);
        Assert.Single(Numeradas(numerado));
    }

    [Fact]
    public void Numerar_LoQueEstaFueraDeUnHunk_NoSeNumera()
    {
        // Antes del primer @@ no hay a qué línea referirse.
        string diff = "Subproject commit abc123\ndiff --git a/A.cs b/A.cs\n@@ -1,1 +1,1 @@\n+x";

        var numeradas = Numeradas(NumeradorDiff.Numerar(diff));

        Assert.Equal(new[] { (1, "+x") }, numeradas);
    }

    [Fact]
    public void Numerar_DiffVacio_DevuelveVacio()
    {
        Assert.Equal(string.Empty, NumeradorDiff.Numerar(string.Empty));
    }

    [Fact]
    public void Numerar_SinDiff_Lanza()
    {
        Assert.Throws<ArgumentNullException>(() => NumeradorDiff.Numerar(null!));
    }

    [Fact]
    public void Numerar_ConservaElNumeroDeLineas()
    {
        // Numerar no puede añadir ni quitar líneas: el modelo vería un diff distinto
        // del que se validó y del que se recortó.
        string diff = string.Join("\n",
            "diff --git a/A.cs b/A.cs",
            "--- a/A.cs",
            "+++ b/A.cs",
            "@@ -1,3 +1,3 @@",
            "     a",
            "-    b",
            "+    c",
            "     d");

        Assert.Equal(
            diff.Split('\n').Length,
            NumeradorDiff.Numerar(diff).Split('\n').Length);
    }

    [Fact]
    public void Numerar_ElResultadoSigueSiendoLegibleParaElFiltro()
    {
        // FiltroRuido lee las cabeceras para saber qué líneas toca cada archivo. Si la
        // numeración las alterase, el filtro descartaría hallazgos correctos.
        string diff = string.Join("\n",
            "diff --git a/src/A.cs b/src/A.cs",
            "--- a/src/A.cs",
            "+++ b/src/A.cs",
            "@@ -10,2 +10,2 @@",
            "+    cambio");

        string numerado = NumeradorDiff.Numerar(diff);

        Assert.Contains("diff --git a/src/A.cs b/src/A.cs", numerado, StringComparison.Ordinal);
        Assert.Contains("+++ b/src/A.cs", numerado, StringComparison.Ordinal);
        Assert.Contains("@@ -10,2 +10,2 @@", numerado, StringComparison.Ordinal);
    }
}
