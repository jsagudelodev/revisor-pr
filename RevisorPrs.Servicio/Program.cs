using Microsoft.Extensions.Options;
using RevisorPrs.Servicio;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<ConfiguracionSondeo>(builder.Configuration.GetSection("Sondeo"));

// Validamos la configuración al arrancar: si falta un valor obligatorio, queremos
// que el servicio falle FUERTE y con mensaje accionable (regla RV.1).
ConfiguracionSondeo configuracionInicial = new();
builder.Configuration.GetSection("Sondeo").Bind(configuracionInicial);
Worker.ValidarConfiguracion(configuracionInicial);

builder.Services.AddSingleton(configuracionInicial);
builder.Services.AddSingleton<IReloj, RelojSistema>();
builder.Services.AddSingleton<IEjecutorVuelta, EjecutorVuelta>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
