using System.Collections.Generic;
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
    /// Diff de ejemplo con dos hunks: uno en el archivo viejo a partir de la línea 10
    /// y otro en el archivo nuevo a partir de la línea 42. Esto da cobertura tanto a
    /// líneas "antiguas" como "nuevas" para la comprobación de presencia en el diff.
    /// </summary>
    private const string DiffEjemplo =
        "@@ -10,3 +10,3 @@\n" +
        "-linea vieja 10\n" +
        "+linea nueva 10\n" +
        "+linea nueva 11\n" +
        "@@ -40,2 +42,3 @@\n" +
        "+linea nueva 42\n" +
        "+linea nueva 43\n" +
        "+linea nueva 44\n";

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
}