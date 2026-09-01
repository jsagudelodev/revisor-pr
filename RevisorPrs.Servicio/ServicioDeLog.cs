using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RevisorPrs.Servicio;

/// <summary>
/// Conecta el proveedor de log rotativo a fichero (RV.21) al sistema de logging
/// sin obligar a Program.cs a conocer el tipo concreto.
///
/// Esta clase existe para evitar que Program.cs importe un
/// <c>LoggingBuilderExtensions</c> externo: la API de Microsoft ya define una con
/// ese nombre, y como <c>Microsoft.Extensions.Logging</c> viene de forma transitiva
/// en el SDK de Worker Service, cualquier PackageReference adicional provoca tipos
/// duplicados (CS0433) y un build ruidoso. Aqui la llamamos <c>ServicioDeLog</c>
/// para evitar la colision sin tocar la SDK del runtime.
/// </summary>
public static class ServicioDeLog
{
    /// <summary>
    /// Resuelve el <see cref="ProveedorRegistrosRotativo"/> registrado en el
    /// contenedor de DI y lo devuelve para anyadirlo como proveedor del sistema
    /// de log. El proveedor se registra en <c>Program.cs</c> como singleton.
    /// </summary>
    public static ILoggerProvider ResolverProveedor(IServiceCollection servicios)
    {
        ArgumentNullException.ThrowIfNull(servicios);

        ProveedorRegistrosRotativo? proveedor = servicios
            .BuildServiceProvider()
            .GetService<ProveedorRegistrosRotativo>();

        if (proveedor is null)
        {
            throw new InvalidOperationException(
                "ProveedorRegistrosRotativo no esta registrado en el contenedor de servicios. " +
                "Anadelo con builder.Services.AddSingleton<ProveedorRegistrosRotativo>(...) antes de llamar a ResolverProveedor.");
        }

        return proveedor;
    }
}