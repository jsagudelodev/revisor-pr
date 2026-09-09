using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RevisorPrs.Servicio;
using Xunit;

namespace RevisorPrs.Tests;

/// <summary>
/// Pruebas del cableado real del servicio (<see cref="ComposicionServicios"/>).
///
/// Existen porque el resto de la suite construye cada pieza a mano y ninguna cubría
/// el contenedor: el servicio podía compilar, pasar sus 107 pruebas y aun así reventar
/// en <c>builder.Build()</c> porque <see cref="Almacen"/> pide un <c>string</c> que el
/// contenedor no sabe resolver. Estas pruebas resuelven de verdad los servicios
/// alojados, que es lo que hace el arranque.
/// </summary>
public class ComposicionServiciosTests : IDisposable
{
    private readonly string _carpetaTemporal =
        Path.Combine(Path.GetTempPath(), "revisorprs-composicion-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Configuración mínima pero válida: la que un operador tendría que rellenar para
    /// que el servicio arranque. La base de datos y el log van a una carpeta temporal
    /// para no ensuciar el directorio de salida de los tests.
    /// </summary>
    private Dictionary<string, string?> ConfiguracionValida() => new()
    {
        ["Sondeo:IntervaloMinutos"] = "5",
        ["Sondeo:Repositorios:0"] = "equipo-a/repo-1",
        ["Bitbucket:MetodoAutenticacion"] = "Basica",
        ["Bitbucket:Usuario"] = "usuario-de-prueba",
        ["Bitbucket:ClaveAplicacion"] = "clave-de-prueba",
        ["Llm:Endpoint"] = "https://api.ejemplo.invalid/v1/chat/completions",
        ["Llm:Modelo"] = "modelo-de-prueba",
        ["Llm:ClaveApi"] = "clave-api-de-prueba",
        ["Llm:SeveridadMinima"] = "media",
        // Puerto 0: lo elige el sistema operativo, así dos tests no chocan.
        ["Estado:Puerto"] = "0",
        ["Registro:RutaFichero"] = Path.Combine(_carpetaTemporal, "revisor-prs.log"),
        ["BaseDatos:RutaBaseDatos"] = Path.Combine(_carpetaTemporal, "composicion.db"),
    };

    private ServiceProvider Construir(Dictionary<string, string?>? ajustes = null)
    {
        Directory.CreateDirectory(_carpetaTemporal);

        IConfiguration configuracion = new ConfigurationBuilder()
            .AddInMemoryCollection(ajustes ?? ConfiguracionValida())
            .Build();

        var servicios = new ServiceCollection();
        servicios.AddLogging();
        ComposicionServicios.Registrar(servicios, configuracion);

        // ValidateOnBuild reproduce la comprobación que hace el host al arrancar:
        // es exactamente la que fallaba con Almacen.
        return servicios.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    [Fact]
    public void Registrar_ConConfiguracionValida_ConstruyeLosServiciosAlojados()
    {
        using var proveedor = Construir();

        var alojados = proveedor.GetServices<IHostedService>().ToList();

        // El sondeo y el endpoint de estado son los dos servicios que arrancan.
        Assert.Contains(alojados, s => s is Worker);
        Assert.Contains(alojados, s => s is ServidorEstado);
    }

    [Fact]
    public void Registrar_ResuelveElAlmacenDesdeLaConfiguracionDeBaseDatos()
    {
        var ajustes = ConfiguracionValida();
        var rutaEsperada = ajustes["BaseDatos:RutaBaseDatos"]!;

        using var proveedor = Construir(ajustes);

        // Resolverlo es la mitad de la prueba: antes lanzaba al no poder inyectar el string.
        var almacen = proveedor.GetRequiredService<IAlmacen>();
        Assert.NotNull(almacen);

        // Y la ruta configurada debe ser la que se usa de verdad, no la de por defecto.
        Assert.True(
            File.Exists(rutaEsperada),
            $"El almacén debería haber creado la base en la ruta configurada ({rutaEsperada}).");
    }

    [Fact]
    public void Registrar_DejaElRecortadorYElFiltroDisponiblesParaElEjecutor()
    {
        using var proveedor = Construir();

        Assert.NotNull(proveedor.GetRequiredService<RecortadorDiff>());
        Assert.NotNull(proveedor.GetRequiredService<FiltroRuido>());
    }

    [Fact]
    public void Registrar_SinRepositorios_FallaConMensajeAccionable()
    {
        var ajustes = ConfiguracionValida();
        ajustes.Remove("Sondeo:Repositorios:0");

        var error = Assert.Throws<InvalidOperationException>(() => Construir(ajustes));

        Assert.Contains("Sondeo.Repositorios", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registrar_ConDireccionDeEstadoPublica_FallaAntesDeAbrirElPuerto()
    {
        var ajustes = ConfiguracionValida();
        ajustes["Estado:Direccion"] = "0.0.0.0";

        var error = Assert.Throws<InvalidOperationException>(() => Construir(ajustes));

        Assert.Contains("loopback", error.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_carpetaTemporal))
            {
                Directory.Delete(_carpetaTemporal, recursive: true);
            }
        }
        catch (IOException)
        {
            // La base SQLite puede seguir bloqueada un instante; no es motivo para fallar.
        }
    }
}
