using System;
using System.Net;

namespace RevisorPrs.Servicio;

/// <summary>
/// Configuración del endpoint que recibe los avisos de Bitbucket (C2).
/// </summary>
/// <remarks>
/// A diferencia del endpoint de estado, este SÍ tiene que ser alcanzable desde fuera:
/// Bitbucket vive en internet. Por eso viene apagado de fábrica y exige un secreto: un
/// endpoint que dispara revisiones —y por tanto gasto en el modelo— no puede quedar
/// abierto a quien pase por ahí.
/// </remarks>
public class ConfiguracionWebhook
{
    /// <summary>
    /// Apagado de fábrica. El sondeo funciona sin esto; el webhook solo cambia la
    /// latencia, así que activarlo tiene que ser una decisión consciente.
    /// </summary>
    public bool Habilitado { get; set; }

    /// <summary>
    /// Dirección de escucha. Por defecto loopback: para que Bitbucket llegue hay que
    /// cambiarla a propósito, normalmente detrás de un proxy inverso con TLS.
    /// </summary>
    public string Direccion { get; set; } = "127.0.0.1";

    /// <summary>Puerto TCP del endpoint.</summary>
    public int Puerto { get; set; } = 8788;

    /// <summary>Ruta que atiende. Cualquier otra devuelve 404.</summary>
    public string Ruta { get; set; } = "/webhook/bitbucket";

    /// <summary>
    /// Secreto compartido con Bitbucket. Obligatorio cuando el webhook está habilitado.
    /// </summary>
    /// <remarks>
    /// Se admiten dos formas de presentarlo, porque no todas las instalaciones de
    /// Bitbucket ofrecen lo mismo:
    /// - Firma HMAC-SHA256 del cuerpo en la cabecera indicada en <see cref="CabeceraFirma"/>.
    /// - El secreto tal cual en la cabecera <c>Authorization: Bearer</c>.
    /// Basta con que una de las dos cuadre.
    /// </remarks>
    public string Secreto { get; set; } = string.Empty;

    /// <summary>
    /// Cabecera donde viaja la firma HMAC del cuerpo. El nombre es configurable porque
    /// depende de la versión de Bitbucket; conviene confirmarlo contra la instalación
    /// concreta antes de darlo por bueno.
    /// </summary>
    public string CabeceraFirma { get; set; } = "X-Hub-Signature";

    /// <summary>
    /// Valida la configuración al arrancar. Si el webhook está habilitado sin secreto,
    /// el servicio debe fallar FUERTE: es preferible no arrancar a exponer un disparador
    /// de gasto sin autenticar.
    /// </summary>
    public static void ValidarConfiguracion(ConfiguracionWebhook? configuracion)
    {
        if (configuracion is null || !configuracion.Habilitado)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(configuracion.Secreto))
        {
            throw new InvalidOperationException(
                "Webhook.Habilitado es true pero falta Webhook.Secreto. Un endpoint que dispara "
                + "revisiones (y por tanto gasto en el modelo) no puede quedar sin autenticar. "
                + "Define Webhook:Secreto con el mismo valor configurado en Bitbucket.");
        }

        // Se admite el 0 igual que en Estado.Puerto: deja que el sistema elija uno libre,
        // que es lo que permite levantar el servidor en pruebas sin chocar entre ellas.
        if (configuracion.Puerto < 0 || configuracion.Puerto > 65_535)
        {
            throw new InvalidOperationException(
                $"Webhook.Puerto está fuera de rango (valor recibido: {configuracion.Puerto}). Usa un puerto entre 1 y 65535, u 0 para que se elija uno libre.");
        }

        if (string.IsNullOrWhiteSpace(configuracion.Ruta) || !configuracion.Ruta.StartsWith('/'))
        {
            throw new InvalidOperationException(
                $"Webhook.Ruta = '{configuracion.Ruta}' no es válida. Debe empezar por '/', por ejemplo '/webhook/bitbucket'.");
        }

        if (string.IsNullOrWhiteSpace(configuracion.Direccion)
            || !IPAddress.TryParse(configuracion.Direccion.Trim(), out _))
        {
            throw new InvalidOperationException(
                $"Webhook.Direccion = '{configuracion.Direccion}' no es una dirección IP válida. "
                + "Usa 127.0.0.1 para pruebas locales, o la interfaz por la que Bitbucket pueda llegar.");
        }
    }
}
