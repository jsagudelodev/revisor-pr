using System;
using System.Linq;
using RevisorPrs.Servicio;
using Xunit;

namespace RevisorPrs.Tests;

/// <summary>
/// Pruebas de qué archivos NO se mandan a revisar (A4).
///
/// Antes se enviaba el diff entero: ficheros de bloqueo de dependencias, código
/// generado, minificados y dependencias vendorizadas entraban igual que el código
/// escrito a mano. Ruido y coste a la vez.
/// </summary>
public class ExclusionRutasTests
{
    [Theory]
    // Ficheros de bloqueo, estén donde estén.
    [InlineData("package-lock.json")]
    [InlineData("web/package-lock.json")]
    [InlineData("apps/portal/yarn.lock")]
    [InlineData("Gemfile.lock")]
    // Dependencias y salidas de compilación, a cualquier profundidad.
    [InlineData("node_modules/left-pad/index.js")]
    [InlineData("web/node_modules/react/index.js")]
    [InlineData("vendor/github.com/pkg/errors/errors.go")]
    [InlineData("src/Servicio/bin/Debug/net8.0/algo.txt")]
    [InlineData("src/Servicio/obj/project.assets.json")]
    [InlineData("dist/app.js")]
    // Minificados, mapas y generados.
    [InlineData("wwwroot/js/app.min.js")]
    [InlineData("wwwroot/css/estilos.min.css")]
    [InlineData("wwwroot/js/app.js.map")]
    [InlineData("Formulario.Designer.cs")]
    [InlineData("src/Modelo.g.cs")]
    [InlineData("api/servicio.pb.go")]
    [InlineData("tests/__snapshots__/vista.snap")]
    public void PorDefecto_ExcluyeLoQueNadieRevisaAMano(string ruta)
    {
        Assert.True(
            ExclusionRutas.EstaExcluida(ruta, ExclusionRutas.PorDefecto),
            $"Se esperaba que '{ruta}' quedara fuera de la revisión.");
    }

    [Theory]
    [InlineData("src/Servicio/Pedido.cs")]
    [InlineData("web/src/componentes/Boton.tsx")]
    [InlineData("README.md")]
    [InlineData("package.json")]
    // Las migraciones NO se excluyen de serie: son generadas, pero esconderlas
    // taparía problemas reales.
    [InlineData("db/migrations/20250101_crear_pedidos.sql")]
    // Un archivo que solo se PARECE a uno excluido sigue revisándose.
    [InlineData("src/vendored/Cliente.cs")]
    [InlineData("src/binarios/Lector.cs")]
    public void PorDefecto_NoExcluyeElCodigoDelEquipo(string ruta)
    {
        Assert.False(
            ExclusionRutas.EstaExcluida(ruta, ExclusionRutas.PorDefecto),
            $"'{ruta}' debería revisarse.");
    }

    [Fact]
    public void UnAsterisco_NoCruzaSeparadoresDeRuta()
    {
        var patrones = new[] { "src/*.cs" };

        Assert.True(ExclusionRutas.EstaExcluida("src/Pedido.cs", patrones));
        Assert.False(ExclusionRutas.EstaExcluida("src/dominio/Pedido.cs", patrones));
    }

    [Fact]
    public void DobleAsterisco_CubreCeroOMasSegmentos()
    {
        var patrones = new[] { "**/dist/**" };

        // También en la raíz, sin ningún segmento por delante.
        Assert.True(ExclusionRutas.EstaExcluida("dist/app.js", patrones));
        Assert.True(ExclusionRutas.EstaExcluida("web/dist/app.js", patrones));
        Assert.True(ExclusionRutas.EstaExcluida("a/b/c/dist/x/y.js", patrones));
        Assert.False(ExclusionRutas.EstaExcluida("web/distribucion/app.js", patrones));
    }

    [Fact]
    public void SinPatrones_NoSeExcluyeNada()
    {
        // Una lista vacía en appsettings.json manda el diff entero, a propósito.
        Assert.False(ExclusionRutas.EstaExcluida("package-lock.json", Array.Empty<string>()));
        Assert.False(ExclusionRutas.EstaExcluida("package-lock.json", null));
    }

    [Fact]
    public void LasRutasSeComparanSinDistinguirMayusculas()
    {
        var patrones = new[] { "*.designer.cs" };

        Assert.True(ExclusionRutas.EstaExcluida("Formulario.Designer.cs", patrones));
        Assert.True(ExclusionRutas.EstaExcluida("FORMULARIO.DESIGNER.CS", patrones));
    }

    [Fact]
    public void ConfiguracionSinLista_UsaLosPatronesDeSerie()
    {
        var config = new ConfiguracionBitbucket();
        Assert.Equal(ExclusionRutas.PorDefecto, config.ResolverRutasExcluidas().ToArray());
    }

    [Fact]
    public void ConfiguracionConListaVacia_DesactivaLaExclusion()
    {
        var config = new ConfiguracionBitbucket { RutasExcluidas = Array.Empty<string>() };
        Assert.Empty(config.ResolverRutasExcluidas());
    }
}
