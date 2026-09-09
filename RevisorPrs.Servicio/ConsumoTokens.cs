namespace RevisorPrs.Servicio;

/// <summary>
/// Tokens consumidos en una revisión.
/// </summary>
/// <remarks>
/// Se mide en tokens y no en dinero porque los tokens son un dato que el proveedor
/// devuelve y que no caduca; los precios cambian y varían por modelo, así que
/// codificarlos en el servicio sería garantizar que envejecen mal. Quien quiera ver una
/// estimación en dinero pone su tarifa en <see cref="ConfiguracionLlm.CostePorMillonEntrada"/>
/// y <see cref="ConfiguracionLlm.CostePorMillonSalida"/>.
///
/// Una revisión puede costar DOS llamadas al modelo: si la primera respuesta no es JSON
/// válido se reintenta. El consumo suma ambas, porque ambas se pagan.
/// </remarks>
/// <param name="Entrada">Tokens del prompt enviado.</param>
/// <param name="Salida">Tokens que generó el modelo.</param>
public readonly record struct ConsumoTokens(int Entrada, int Salida)
{
    /// <summary>Consumo desconocido o nulo.</summary>
    public static ConsumoTokens Ninguno => default;

    /// <summary>Total facturable de la revisión.</summary>
    public int Total => Entrada + Salida;

    /// <summary>Suma dos consumos: el del primer intento y el del reintento.</summary>
    public static ConsumoTokens operator +(ConsumoTokens a, ConsumoTokens b)
        => new(a.Entrada + b.Entrada, a.Salida + b.Salida);

    /// <summary>
    /// Estimación en dinero con las tarifas indicadas, o 0 si no se han configurado.
    /// </summary>
    public decimal Estimar(decimal costePorMillonEntrada, decimal costePorMillonSalida)
        => (Entrada * costePorMillonEntrada + Salida * costePorMillonSalida) / 1_000_000m;
}
