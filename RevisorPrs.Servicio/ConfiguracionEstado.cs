using System;

namespace RevisorPrs.Servicio;

/// <summary>
/// Configuración del endpoint local de estado (RV.20).
/// </summary>
public class ConfiguracionEstado
{
    /// <summary>
    /// Dirección de escucha. Solo se admiten direcciones de loopback (127.0.0.1, ::1 o
    /// localhost). Cualquier otro valor se rechaza al arrancar: el endpoint de estado
    /// NUNCA debe quedar expuesto en una interfaz pública.
    /// </summary>
    public string Direccion { get; set; } = "127.0.0.1";

    /// <summary>
    /// Puerto TCP del endpoint. Con 0 se elige uno libre al arrancar (útil en tests).
    /// </summary>
    public int Puerto { get; set; } = 8787;

    /// <summary>
    /// Permite apagar el endpoint sin tocar el resto de la configuración.
    /// </summary>
    public bool Habilitado { get; set; } = true;

    /// <summary>
    /// Valida la configuración del endpoint al arrancar: si la dirección no es de
    /// loopback, el servicio debe fallar FUERTE y con mensaje accionable en lugar de
    /// quedarse escuchando en una interfaz pública.
    /// </summary>
    public static void ValidarConfiguracion(ConfiguracionEstado? configuracion)
    {
        if (configuracion is null)
        {
            throw new InvalidOperationException(
                "Falta la sección 'Estado' en la configuración. Añade 'Estado: { Direccion, Puerto, Habilitado }' al appsettings.json.");
        }

        ServidorEstado.ValidarDireccion(configuracion.Direccion);

        if (configuracion.Puerto < 0 || configuracion.Puerto > 65_535)
        {
            throw new InvalidOperationException(
                $"Estado.Puerto está fuera de rango (valor recibido: {configuracion.Puerto}). Usa un puerto entre 1 y 65535, u 0 para que se elija uno libre.");
        }
    }
}