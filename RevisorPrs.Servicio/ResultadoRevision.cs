using System.Collections.Generic;

namespace RevisorPrs.Servicio;

/// <summary>
/// Resultado de una revisión de diff por parte del LLM.
///
/// Decisión de diseño (RV.10): en lugar de lanzar excepciones o devolver listas vacías
/// ambiguas cuando el LLM responde con basura, la revisión se modela como un valor
/// con tres campos:
///   - <see cref="Exito"/>: true si se extrajo un JSON válido (con o sin hallazgos);
///     false si, tras UN reintento pidiendo solo JSON, la respuesta seguía sin ser válida.
///   - <see cref="Hallazgos"/>: lista de hallazgos encontrados. Si <see cref="Exito"/> es
///     false, esta lista está SIEMPRE vacía: nunca se publica basura en un PR.
///   - <see cref="Motivo"/>: motivo del fallo (null si <see cref="Exito"/> es true).
///     Pensado para que el Worker/llamador pueda registrar la incidencia sin propagar
///     la excepción ni filtrar el contenido bruto del LLM.
///
/// Se usa un record para inmutabilidad y para que la igualdad estructural facilite los
/// asserts de los tests.
/// </summary>
public record ResultadoRevision(
    bool Exito,
    IReadOnlyList<Hallazgo> Hallazgos,
    string? Motivo)
{
    /// <summary>
    /// Crea un resultado exitoso con la lista de hallazgos indicada (puede ser vacía).
    /// </summary>
    public static ResultadoRevision Ok(IReadOnlyList<Hallazgo> hallazgos)
        => new(Exito: true, Hallazgos: hallazgos, Motivo: null);

    /// <summary>
    /// Crea un resultado fallido con un motivo legible. La lista de hallazgos
    /// queda FORZADA a vacía para impedir que el llamador publique basura en un PR.
    /// </summary>
    public static ResultadoRevision Fallo(string motivo)
        => new(Exito: false, Hallazgos: System.Array.Empty<Hallazgo>(), Motivo: motivo);
}