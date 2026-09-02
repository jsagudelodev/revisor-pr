using System;
using System.Collections.Generic;
using System.Linq;

namespace RevisorPrs.Servicio;

/// <summary>
/// Lista de valores sensibles (claves de API, contraseñas de aplicación, usuarios) que
/// NUNCA deben salir del proceso en claro: ni por el log ni por el endpoint /estado
/// (RV.20). El saneador los sustituye por una marca fija dondequiera que aparezcan.
///
/// Se aplica sobre los mensajes de error y sobre el cuerpo JSON completo de la respuesta,
/// de modo que un secreto que se cuele por una vía inesperada (una excepción que incluye
/// la cabecera de autenticación, por ejemplo) siga sin publicarse.
/// </summary>
public sealed class SaneadorSecretos
{
    /// <summary>
    /// Texto con el que se reemplaza cualquier valor sensible detectado.
    /// </summary>
    public const string Marca = "***";

    private readonly IReadOnlyList<string> _secretos;

    public SaneadorSecretos(IEnumerable<string?> secretos)
    {
        _secretos = (secretos ?? Enumerable.Empty<string?>())
            .Where(EsValido)
            .Select(s => s!)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(s => s.Length)
            .ToArray();
    }

    /// <summary>
    /// Lista vacía: nada que enmascarar. Útil en tests y cuando no hay configuración.
    /// </summary>
    public static SaneadorSecretos Ninguno { get; } = new SaneadorSecretos(Array.Empty<string>());

    /// <summary>
    /// Cuántos valores sensibles se vigilarán. Los vacíos no cuentan.
    /// </summary>
    public int CantidadSecretos => _secretos.Count;

    /// <summary>
    /// Devuelve el texto con todos los valores sensibles sustituidos por <see cref="Marca"/>.
    /// </summary>
    public string Sanear(string? texto)
    {
        if (string.IsNullOrEmpty(texto))
        {
            return string.Empty;
        }

        string resultado = texto;
        foreach (string secreto in _secretos)
        {
            resultado = resultado.Replace(secreto, Marca, StringComparison.Ordinal);
        }

        return resultado;
    }

    /// <summary>
    /// Indica si el texto contiene alguno de los valores sensibles en claro.
    /// </summary>
    public bool ContieneSecreto(string? texto)
    {
        if (string.IsNullOrEmpty(texto))
        {
            return false;
        }

        foreach (string secreto in _secretos)
        {
            if (texto.Contains(secreto, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool EsValido(string? secreto) => !string.IsNullOrWhiteSpace(secreto);

    /// <summary>
    /// Reúne los valores sensibles declarados en la configuración (clave del LLM y
    /// credenciales de Bitbucket). Se leen por nombre de clave para no depender de
    /// las clases de opciones ni de que estén rellenas.
    /// </summary>
    public static SaneadorSecretos DesdeConfiguracion(Microsoft.Extensions.Configuration.IConfiguration configuracion)
    {
        ArgumentNullException.ThrowIfNull(configuracion);

        return new SaneadorSecretos(new[]
        {
            configuracion["Llm:ClaveApi"],
            configuracion["Bitbucket:ClaveAplicacion"],
            configuracion["Bitbucket:Usuario"],
            configuracion["Bitbucket:Token"],
        });
    }
}