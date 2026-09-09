using System;
using RevisorPrs.Servicio;
using Xunit;

namespace RevisorPrs.Tests;

/// <summary>
/// Pruebas del texto que ve el equipo en el pull request (A1).
///
/// Antes se publicaba únicamente el <see cref="Hallazgo.Resumen"/>. El prompt pide al
/// modelo un <see cref="Hallazgo.Detalle"/> con la justificación y la sugerencia de
/// arreglo: se pagaba por generarlo y se descartaba antes de llegar a nadie.
/// </summary>
public class FormateadorComentarioTests
{
    [Fact]
    public void Componer_Anclado_LlevaSeveridadResumenYDetalle()
    {
        var hallazgo = new Hallazgo(
            "src/Pedido.cs",
            42,
            "error",
            "El índice puede desbordarse",
            "El bucle recorre hasta `longitud` inclusive; usa `<` en lugar de `<=`.");

        string cuerpo = FormateadorComentario.Componer(hallazgo, anclado: true);

        Assert.Equal(
            "**Error** · El índice puede desbordarse\n\n"
            + "El bucle recorre hasta `longitud` inclusive; usa `<` en lugar de `<=`.",
            cuerpo);
    }

    [Fact]
    public void Componer_Anclado_NoRepiteArchivoNiLinea()
    {
        // Bitbucket ya los muestra junto al comentario anclado.
        var hallazgo = new Hallazgo("src/Pedido.cs", 42, "error", "resumen", "detalle");

        string cuerpo = FormateadorComentario.Componer(hallazgo, anclado: true);

        Assert.DoesNotContain("src/Pedido.cs", cuerpo, StringComparison.Ordinal);
        Assert.DoesNotContain("42", cuerpo, StringComparison.Ordinal);
    }

    [Fact]
    public void Componer_SinAncla_LlevaLaUbicacionDentro()
    {
        var hallazgo = new Hallazgo("src/Pedido.cs", null, "warning", "resumen", "detalle");

        string cuerpo = FormateadorComentario.Componer(hallazgo, anclado: false);

        Assert.Equal("**Aviso** · `src/Pedido.cs` · resumen\n\ndetalle", cuerpo);
    }

    [Fact]
    public void Componer_SinAncla_ConLinea_LaIncluyeEnLaUbicacion()
    {
        // Un hallazgo con línea que no se pudo anclar (por ejemplo, sin archivo válido
        // para Bitbucket) conserva la referencia dentro del texto.
        var hallazgo = new Hallazgo("src/Pedido.cs", 7, "info", "resumen", "detalle");

        string cuerpo = FormateadorComentario.Componer(hallazgo, anclado: false);

        Assert.StartsWith("**Nota** · `src/Pedido.cs:7` · resumen", cuerpo, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("error", "**Error**")]
    [InlineData("alta", "**Error**")]
    [InlineData("warning", "**Aviso**")]
    [InlineData("media", "**Aviso**")]
    [InlineData("info", "**Nota**")]
    [InlineData("baja", "**Nota**")]
    public void Componer_TraduceLosDosVocabulariosDeSeveridad(string severidad, string esperado)
    {
        // El modelo responde en inglés y el umbral se configura en español: ambos
        // vocabularios tienen que llegar a la misma etiqueta.
        var hallazgo = new Hallazgo("a.cs", 1, severidad, "resumen", string.Empty);

        Assert.StartsWith(esperado, FormateadorComentario.Componer(hallazgo, anclado: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Componer_SeveridadDesconocida_PasaANota()
    {
        // Antes se enseñaba el texto del modelo tal cual, para no perder información.
        // Se cambió al hacer B4: esa etiqueta se publica en negrita dentro de un
        // comentario Markdown, y el texto lo escribe el modelo leyendo un diff que
        // controla quien abre el pull request.
        var hallazgo = new Hallazgo("a.cs", 1, "crítico", "resumen", string.Empty);

        Assert.StartsWith("**Nota**", FormateadorComentario.Componer(hallazgo, anclado: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Componer_SeveridadConMarkdown_NoLlegaAlComentario()
    {
        // El caso que motiva la regla: severidad usada para inyectar Markdown.
        var hallazgo = new Hallazgo(
            "a.cs", 1, "[pincha aquí](http://malicioso.invalid)", "resumen", string.Empty);

        string cuerpo = FormateadorComentario.Componer(hallazgo, anclado: true);

        Assert.StartsWith("**Nota**", cuerpo, StringComparison.Ordinal);
        Assert.DoesNotContain("malicioso.invalid", cuerpo, StringComparison.Ordinal);
    }

    [Fact]
    public void Componer_SinDetalle_NoDejaBloqueVacio()
    {
        var hallazgo = new Hallazgo("a.cs", 1, "error", "resumen", string.Empty);

        string cuerpo = FormateadorComentario.Componer(hallazgo, anclado: true);

        Assert.Equal("**Error** · resumen", cuerpo);
        Assert.DoesNotContain("\n", cuerpo, StringComparison.Ordinal);
    }

    [Fact]
    public void Componer_DetalleQueRepiteElResumen_NoSeDuplica()
    {
        // Los modelos a veces rellenan ambos campos con la misma frase.
        var hallazgo = new Hallazgo("a.cs", 1, "error", "Falta validar la entrada", "  falta validar la entrada ");

        string cuerpo = FormateadorComentario.Componer(hallazgo, anclado: true);

        Assert.Equal("**Error** · Falta validar la entrada", cuerpo);
    }

    [Fact]
    public void Componer_SinHallazgo_Lanza()
    {
        Assert.Throws<ArgumentNullException>(() => FormateadorComentario.Componer(null!, anclado: true));
    }
}
