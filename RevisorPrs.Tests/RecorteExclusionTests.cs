using System;
using System.Text;
using RevisorPrs.Servicio;
using Xunit;

namespace RevisorPrs.Tests;

/// <summary>
/// Pruebas de que <see cref="RecortadorDiff"/> quita del diff los archivos excluidos
/// antes de mandarlo al modelo (A4).
/// </summary>
public class RecorteExclusionTests
{
    private static string Seccion(string ruta, string relleno) =>
        $"diff --git a/{ruta} b/{ruta}\n@@ -1,1 +1,1 @@\n+{relleno}\n";

    [Fact]
    public void Recortar_QuitaLosArchivosExcluidos_YConservaElCodigo()
    {
        string diff =
            Seccion("package-lock.json", new string('x', 100)) +
            Seccion("src/Pedido.cs", "cambio de verdad") +
            Seccion("web/node_modules/react/index.js", new string('y', 100));

        var recortador = new RecortadorDiff(new ConfiguracionBitbucket());

        string resultado = recortador.Recortar(diff);

        Assert.Contains("src/Pedido.cs", resultado, StringComparison.Ordinal);
        Assert.DoesNotContain("package-lock.json", resultado, StringComparison.Ordinal);
        Assert.DoesNotContain("node_modules", resultado, StringComparison.Ordinal);
    }

    [Fact]
    public void Recortar_LaExclusionSeAplicaAntesDelTopeDeBytes()
    {
        // Un fichero de bloqueo enorme por delante del código. Si la exclusión llegara
        // después del tope, el bloqueo se comería el presupuesto y el código quedaría
        // fuera, que es exactamente lo que pasaba antes.
        string diff =
            Seccion("package-lock.json", new string('x', 5000)) +
            Seccion("src/Pedido.cs", "cambio de verdad");

        var recortador = new RecortadorDiff(new ConfiguracionBitbucket { TopeBytesDiff = 1000 });

        string resultado = recortador.Recortar(diff);

        Assert.Contains("cambio de verdad", resultado, StringComparison.Ordinal);
        Assert.DoesNotContain("package-lock.json", resultado, StringComparison.Ordinal);
    }

    [Fact]
    public void Recortar_SiTodoEstaExcluido_DevuelveVacio()
    {
        // Un PR que solo toca ficheros de bloqueo no tiene nada que revisar. Al devolver
        // vacío, el ejecutor lo da por revisado sin gastar una llamada al modelo.
        string diff =
            Seccion("package-lock.json", "a") +
            Seccion("yarn.lock", "b");

        var recortador = new RecortadorDiff(new ConfiguracionBitbucket());

        Assert.Equal(string.Empty, recortador.Recortar(diff));
    }

    [Fact]
    public void Recortar_SinExclusionesYCabiendo_DevuelveElDiffIntacto()
    {
        // El camino de siempre no debe cambiar ni un byte.
        string diff = Seccion("src/Pedido.cs", "cambio");

        var recortador = new RecortadorDiff(new ConfiguracionBitbucket());

        Assert.Equal(diff, recortador.Recortar(diff));
    }

    [Fact]
    public void Recortar_ConExclusionDesactivada_MandaElDiffEntero()
    {
        string diff =
            Seccion("package-lock.json", "a") +
            Seccion("src/Pedido.cs", "b");

        var recortador = new RecortadorDiff(new ConfiguracionBitbucket
        {
            RutasExcluidas = Array.Empty<string>(),
        });

        Assert.Equal(diff, recortador.Recortar(diff));
    }

    [Fact]
    public void Recortar_LosExcluidosNoSeAnuncianDentroDelDiff()
    {
        // La nota "[RecortadorDiff]" es para lo que se cae por tamaño. Las exclusiones
        // son política nuestra: van al log, no al prompt, para no gastar tokens.
        string diff =
            Seccion("package-lock.json", "a") +
            Seccion("src/Pedido.cs", "b");

        var recortador = new RecortadorDiff(new ConfiguracionBitbucket());

        Assert.DoesNotContain("[RecortadorDiff]", recortador.Recortar(diff), StringComparison.Ordinal);
    }
}
