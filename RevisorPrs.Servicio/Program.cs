using RevisorPrs.Servicio;

var builder = Host.CreateApplicationBuilder(args);

// Todo el cableado y la validación de configuración viven en ComposicionServicios,
// para que un test pueda montar exactamente el mismo contenedor que producción.
ComposicionServicios.Registrar(builder.Services, builder.Configuration);

var host = builder.Build();
host.Run();
