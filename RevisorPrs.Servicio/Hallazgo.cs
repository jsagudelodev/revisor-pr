namespace RevisorPrs.Servicio;

/// <summary>
/// Representa un hallazgo encontrado durante la revisión de un pull request.
/// </summary>
/// <param name="Archivo">Ruta del archivo donde se encontró el hallazgo.</param>
/// <param name="Linea">Número de línea donde se encontró el hallazgo (puede ser null).</param>
/// <param name="Severidad">Nivel de severidad (ej: "error", "warning", "info").</param>
/// <param name="Resumen">Descripción breve del hallazgo.</param>
/// <param name="Detalle">Descripción detallada del hallazgo.</param>
public record Hallazgo(string Archivo, int? Linea, string Severidad, string Resumen, string Detalle);