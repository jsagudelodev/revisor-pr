using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;
using RevisorPrs.Servicio;

namespace RevisorPrs.Tests;

/// <summary>
/// Pruebas del <see cref="FiltroRuido"/>: verifican que se descartan los hallazgos
/// por debajo de la severidad umbral y los que apuntan a líneas que no aparecen
/// en el diff, y que los hallazgos sin línea se conservan.
/// </summary>
public class FiltroRuidoTests
{
    /// <summary>
    /// Diff de ejemplo con tres archivos, cada uno con dos hunks: uno en el archivo
    /// viejo a partir de la línea 10 y otro en el nuevo a partir de la 42. Da cobertura
    /// tanto a líneas "antiguas" como "nuevas".
    ///
    /// Lleva cabeceras <c>diff --git</c> reales porque el filtro comprueba la línea
    /// contra los hunks DE SU ARCHIVO: sin ellas no habría archivo al que atribuirlos y
    /// estos tests no ejercitarían la regla que de verdad importa.
    /// </summary>
    private static readonly string DiffEjemplo =
        SeccionDeEjemplo("src/A.cs") +
        SeccionDeEjemplo("src/B.cs") +
        SeccionDeEjemplo("src/C.cs");

    private const string HunksDeEjemplo =
        "@@ -10,3 +10,3 @@\n" +
        "-linea vieja 10\n" +
        "+linea nueva 10\n" +
        "+linea nueva 11\n" +
        "@@ -40,2 +42,3 @@\n" +
        "+linea nueva 42\n" +
        "+linea nueva 43\n" +
        "+linea nueva 44\n";

    private static string SeccionDeEjemplo(string ruta) =>
        "diff --git a/" + ruta + " b/" + ruta + "\n" +
        "--- a/" + ruta + "\n" +
        "+++ b/" + ruta + "\n" +
        HunksDeEjemplo;

    [Fact]
    public void Filtrar_ConSeveridadPorDebajoDelUmbral_LoDescarta()
    {
        // Umbral "media": un hallazgo "baja" debe quedarse fuera.
        var config = Options.Create(new ConfiguracionLlm { SeveridadMinima = "media" });
        var logger = new RegistradorFalso<FiltroRuido>();
        var filtro = new FiltroRuido(config, logger);

        var hallazgos = new List<Hallazgo>
        {
            new("src/A.cs", Linea: 42, Severidad: "baja", Resumen: "Ruido menor", Detalle: "detalle"),
            new("src/B.cs", Linea: 10, Severidad: "media", Resumen: "Conservado", Detalle: "detalle"),
        };

        var resultado = filtro.Filtrar(hallazgos, DiffEjemplo);

        Assert.Single(resultado);
        Assert.Equal("Conservado", resultado[0].Resumen);
        // El descarte debe quedar registrado en el log con su motivo.
        Assert.Contains(logger.Mensajes, m => m.Contains("severidad por debajo del umbral"));
    }

    [Fact]
    public void Filtrar_ConLineaQueNoEstaEnElDiff_LoDescarta()
    {
        // Umbral "baja" para que NUNCA se descarte por severidad en este test
        // y poder aislar la regla de la línea.
        var config = Options.Create(new ConfiguracionLlm { SeveridadMinima = "baja" });
        var logger = new RegistradorFalso<FiltroRuido>();
        var filtro = new FiltroRuido(config, logger);

        var hallazgos = new List<Hallazgo>
        {
            // 999 no aparece en ningún hunk del DiffEjemplo.
            new("src/A.cs", Linea: 999, Severidad: "alta", Resumen: "Apunta fuera", Detalle: "detalle"),
            // 42 sí aparece (línea nueva del segundo hunk).
            new("src/B.cs", Linea: 42, Severidad: "alta", Resumen: "Apunta dentro", Detalle: "detalle"),
        };

        var resultado = filtro.Filtrar(hallazgos, DiffEjemplo);

        Assert.Single(resultado);
        Assert.Equal("Apunta dentro", resultado[0].Resumen);
        Assert.Contains(logger.Mensajes, m => m.Contains("línea fuera del diff"));
    }

    [Fact]
    public void Filtrar_SinLinea_LoConserva()
    {
        // Un hallazgo sin línea (Linea == null) es un comentario general: aunque la
        // "línea" no esté en el diff, NO se descarta. La línea null indica que el
        // LLM habla del archivo en su conjunto, no de un punto concreto.
        var config = Options.Create(new ConfiguracionLlm { SeveridadMinima = "baja" });
        var logger = new RegistradorFalso<FiltroRuido>();
        var filtro = new FiltroRuido(config, logger);

        var hallazgos = new List<Hallazgo>
        {
            new("src/A.cs", Linea: null, Severidad: "media", Resumen: "Comentario general", Detalle: "detalle"),
        };

        var resultado = filtro.Filtrar(hallazgos, DiffEjemplo);

        Assert.Single(resultado);
        Assert.Equal("Comentario general", resultado[0].Resumen);
        // No debe haberse emitido ningún descarte.
        Assert.DoesNotContain(logger.Mensajes, m => m.Contains("Hallazgo descartado"));
    }

    [Fact]
    public void Filtrar_ConSeveridadesDelModelo_AplicaElUmbralConfigurado()
    {
        // El umbral se configura en español ("media") pero el modelo responde con el
        // vocabulario que le pide PromptRevision ("info"/"warning"/"error"). Si el
        // filtro no equiparara ambos, todo hallazgo real pesaría menos que el umbral
        // y se descartarían TODOS.
        var config = Options.Create(new ConfiguracionLlm { SeveridadMinima = "media" });
        var logger = new RegistradorFalso<FiltroRuido>();
        var filtro = new FiltroRuido(config, logger);

        var hallazgos = new List<Hallazgo>
        {
            new("src/A.cs", Linea: 42, Severidad: "error", Resumen: "Grave", Detalle: "detalle"),
            new("src/B.cs", Linea: 42, Severidad: "warning", Resumen: "Aviso", Detalle: "detalle"),
            new("src/C.cs", Linea: 42, Severidad: "info", Resumen: "Ruido", Detalle: "detalle"),
        };

        var resultado = filtro.Filtrar(hallazgos, DiffEjemplo);

        // "error" y "warning" llegan al umbral "media"; "info" se queda por debajo.
        Assert.Equal(2, resultado.Count);
        Assert.Equal(new[] { "Grave", "Aviso" }, resultado.Select(h => h.Resumen).ToArray());
        Assert.Contains(logger.Mensajes, m => m.Contains("severidad por debajo del umbral"));
    }

    [Fact]
    public void Filtrar_ConLineaInteriorDelHunk_LaConserva()
    {
        // La cabecera "@@ -40,2 +42,3 @@" declara un tramo de tres líneas (42, 43 y 44),
        // no solo la primera. Un hallazgo en la 43 está dentro del diff.
        var config = Options.Create(new ConfiguracionLlm { SeveridadMinima = "baja" });
        var logger = new RegistradorFalso<FiltroRuido>();
        var filtro = new FiltroRuido(config, logger);

        var hallazgos = new List<Hallazgo>
        {
            new("src/A.cs", Linea: 43, Severidad: "alta", Resumen: "Interior del hunk", Detalle: "detalle"),
            new("src/B.cs", Linea: 44, Severidad: "alta", Resumen: "Final del hunk", Detalle: "detalle"),
            // 45 queda justo fuera del tramo 42..44.
            new("src/C.cs", Linea: 45, Severidad: "alta", Resumen: "Pasado el hunk", Detalle: "detalle"),
        };

        var resultado = filtro.Filtrar(hallazgos, DiffEjemplo);

        Assert.Equal(new[] { "Interior del hunk", "Final del hunk" }, resultado.Select(h => h.Resumen).ToArray());
        Assert.Contains(logger.Mensajes, m => m.Contains("línea fuera del diff"));
    }
}