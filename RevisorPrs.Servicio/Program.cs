using Microsoft.Extensions.Options;
using RevisorPrs.Servicio;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<ConfiguracionSondeo>(builder.Configuration.GetSection("Sondeo"));
builder.Services.Configure<ConfiguracionBitbucket>(builder.Configuration.GetSection("Bitbucket"));
builder.Services.Configure<ConfiguracionLlm>(builder.Configuration.GetSection("Llm"));

// Validamos la configuración al arrancar: si falta un valor obligatorio, queremos
// que el servicio falle FUERTE y con mensaje accionable (regla RV.1).
ConfiguracionSondeo configuracionInicial = new();
builder.Configuration.GetSection("Sondeo").Bind(configuracionInicial);
Worker.ValidarConfiguracion(configuracionInicial);

ConfiguracionLlm configuracionLlmInicial = new();
builder.Configuration.GetSection("Llm").Bind(configuracionLlmInicial);
Revisor.ValidarConfiguracion(configuracionLlmInicial);

// El endpoint /estado (RV.20) solo puede escucharse en loopback: si la configuración
// pide una interfaz pública, el servicio falla aquí en lugar de exponer el estado.
ConfiguracionEstado configuracionEstadoInicial = new();
builder.Configuration.GetSection("Estado").Bind(configuracionEstadoInicial);
ConfiguracionEstado.ValidarConfiguracion(configuracionEstadoInicial);

builder.Services.AddSingleton(configuracionInicial);
builder.Services.AddSingleton(configuracionLlmInicial);
builder.Services.AddSingleton(configuracionEstadoInicial);
builder.Services.AddSingleton<EstadoServicio>();
builder.Services.AddSingleton(sp => SaneadorSecretos.DesdeConfiguracion(builder.Configuration));
builder.Services.AddSingleton<IReloj, RelojSistema>();
builder.Services.AddSingleton<IAlmacen, Almacen>();
builder.Services.AddSingleton<DecisorRevisar>(sp => new DecisorRevisar(
    sp.GetRequiredService<ILogger<DecisorRevisar>>(),
    sp.GetRequiredService<IAlmacen>()));
builder.Services.AddSingleton<IEjecutorVuelta, EjecutorVuelta>();
builder.Services.AddSingleton<TraductorEventoPr>();
builder.Services.AddHttpClient<IClienteBitbucket, ClienteBitbucket>();
builder.Services.AddHttpClient<IRevisor, Revisor>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<ServidorEstado>();

var host = builder.Build();
host.Run();
