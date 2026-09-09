using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

namespace RevisorPrs.Servicio;

/// <summary>
/// Registra en el contenedor todas las piezas del servicio y valida la configuración
/// al arrancar.
///
/// Vive fuera de <c>Program.cs</c> a propósito: las instrucciones de nivel superior
/// no se pueden invocar desde los tests, así que el cableado real del servicio no
/// tenía ninguna prueba que lo cubriera. Con el registro aquí, un test puede montar
/// el mismo contenedor que usa producción y comprobar que todo se construye.
/// </summary>
public static class ComposicionServicios
{
    /// <summary>
    /// Nombre con el que el servicio se registra en el Administrador de control de
    /// servicios de Windows.
    /// </summary>
    public const string NombreServicio = "RevisorPrs";

    /// <summary>
    /// Valida la configuración y registra todos los servicios. Si falta un valor
    /// obligatorio, lanza <see cref="System.InvalidOperationException"/> con un mensaje
    /// accionable: el servicio debe fallar FUERTE en el arranque (regla RV.1).
    /// </summary>
    public static void Registrar(IServiceCollection servicios, IConfiguration configuracion)
    {
        ArgumentNullException.ThrowIfNull(servicios);
        ArgumentNullException.ThrowIfNull(configuracion);

        // El README lo describe como servicio de Windows y esto es lo que lo hace serlo:
        // sin ello, el Administrador de control de servicios arranca el proceso, nunca
        // recibe la confirmación que espera y da el arranque por fallido pasado el plazo.
        // Fuera de un servicio de Windows (consola, tests) no hace nada.
        servicios.AddWindowsService(opciones => opciones.ServiceName = NombreServicio);

        servicios.Configure<ConfiguracionSondeo>(configuracion.GetSection("Sondeo"));
        servicios.Configure<ConfiguracionBitbucket>(configuracion.GetSection("Bitbucket"));
        servicios.Configure<ConfiguracionLlm>(configuracion.GetSection("Llm"));

        ConfiguracionSondeo configuracionSondeo = new();
        configuracion.GetSection("Sondeo").Bind(configuracionSondeo);
        Worker.ValidarConfiguracion(configuracionSondeo);

        ConfiguracionLlm configuracionLlm = new();
        configuracion.GetSection("Llm").Bind(configuracionLlm);
        Revisor.ValidarConfiguracion(configuracionLlm);

        ConfiguracionBitbucket configuracionBitbucket = new();
        configuracion.GetSection("Bitbucket").Bind(configuracionBitbucket);
        ClienteBitbucket.ValidarConfiguracion(configuracionBitbucket);

        // El endpoint /estado (RV.20) solo puede escucharse en loopback: si la configuración
        // pide una interfaz pública, el servicio falla aquí en lugar de exponer el estado.
        ConfiguracionEstado configuracionEstado = new();
        configuracion.GetSection("Estado").Bind(configuracionEstado);
        ConfiguracionEstado.ValidarConfiguracion(configuracionEstado);

        ConfiguracionRegistro configuracionRegistro = new();
        configuracion.GetSection("Registro").Bind(configuracionRegistro);
        ConfiguracionRegistro.ValidarConfiguracion(configuracionRegistro);

        // El webhook viene apagado de fabrica. Si se enciende sin secreto, el servicio
        // NO arranca: un endpoint que dispara revisiones (y gasto en el modelo) no puede
        // quedar sin autenticar.
        ConfiguracionWebhook configuracionWebhook = new();
        configuracion.GetSection("Webhook").Bind(configuracionWebhook);
        ConfiguracionWebhook.ValidarConfiguracion(configuracionWebhook);

        ConfiguracionBaseDatos configuracionBaseDatos = new();
        configuracion.GetSection("BaseDatos").Bind(configuracionBaseDatos);

        servicios.AddSingleton(configuracionSondeo);
        servicios.AddSingleton(configuracionLlm);
        // ConfiguracionBitbucket se registra además como instancia suelta porque
        // RecortadorDiff la recibe directamente, no envuelta en IOptions.
        servicios.AddSingleton(configuracionBitbucket);
        servicios.AddSingleton(configuracionEstado);
        servicios.AddSingleton(configuracionRegistro);
        servicios.AddSingleton(configuracionBaseDatos);
        servicios.AddSingleton(configuracionWebhook);
        servicios.AddSingleton<ColaDeRevisiones>();

        servicios.AddSingleton<EstadoServicio>();
        servicios.AddSingleton(sp => SaneadorSecretos.DesdeConfiguracion(configuracion));
        servicios.AddSingleton<IReloj, RelojSistema>();

        // Almacen recibe una ruta (string), que el contenedor no sabe resolver:
        // se construye con una fábrica a partir de ConfiguracionBaseDatos.
        servicios.AddSingleton<IAlmacen>(sp => new Almacen(
            sp.GetRequiredService<ConfiguracionBaseDatos>().RutaBaseDatos));

        // Sin eco a consola: el host ya trae su propio proveedor de consola, asi que
        // pasarle Console.Out hacia que cada linea apareciera dos veces.
        servicios.AddSingleton<ProveedorRegistrosRotativo>(sp => new ProveedorRegistrosRotativo(
            sp.GetRequiredService<ConfiguracionRegistro>(),
            sp.GetRequiredService<SaneadorSecretos>()));

        // El sistema de log recoge de aquí todos los ILoggerProvider registrados, así que
        // basta con exponer el rotativo como tal. Antes se resolvía construyendo un
        // ServiceProvider aparte solo para sacarlo, lo que creaba un juego entero de
        // singletons duplicados que nadie liberaba nunca.
        servicios.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<ProveedorRegistrosRotativo>());

        servicios.AddSingleton<DecisorRevisar>(sp => new DecisorRevisar(
            sp.GetRequiredService<ILogger<DecisorRevisar>>(),
            sp.GetRequiredService<IAlmacen>()));

        // RecortadorDiff acota el diff que se envía al modelo (Bitbucket.TopeBytesDiff)
        // y FiltroRuido descarta los hallazgos que no merecen un comentario
        // (Llm.SeveridadMinima y líneas fuera del diff). Ambos los consume EjecutorVuelta.
        servicios.AddSingleton<RecortadorDiff>();
        servicios.AddSingleton<FiltroRuido>();

        servicios.AddSingleton<IEjecutorVuelta, EjecutorVuelta>();
        servicios.AddSingleton<TraductorEventoPr>();
        servicios.AddHttpClient<IClienteBitbucket, ClienteBitbucket>();
        servicios.AddHttpClient<IRevisor, Revisor>();
        servicios.AddHostedService<Worker>();
        servicios.AddHostedService<ServidorEstado>();
        servicios.AddHostedService<ServidorWebhook>();
    }
}
