using System;
using System.IO;
using System.Linq;

namespace RevisorPrs.Servicio;

/// <summary>
/// Rota el fichero de log activo cuando supera el tamano maximo y conserva
/// solo los N ficheros mas recientes (RV.21). Pensado para inyectar el reloj
/// y la funcion de tamano del fichero, de modo que los tests puedan
/// verificar la rotacion sin escribir cientos de megas ni esperar un dia.
///
/// Esquema de ficheros: el activo es <c>ruta</c>; los rotados son
/// <c>ruta.1</c>, <c>ruta.2</c>, ..., <c>ruta.N-1</c>. Tras rotar:
/// - el activo actual pasa a <c>ruta.1</c>;
/// - <c>ruta.1</c> pasa a <c>ruta.2</c>; y asi sucesivamente;
/// - el mas antiguo (<c>ruta.N-1</c>) se elimina si supera el limite.
/// </summary>
public sealed class RotadorRegistros
{
    private readonly string _rutaActiva;
    private readonly int _ficherosConservados;
    private readonly long _tamanoMaximoBytes;
    private readonly Func<long>? _tamanoFichero;
    private readonly object _candado = new();

    /// <summary>
    /// Crea un rotador a partir de la configuracion. La rotacion se evalua
    /// cada vez que se llama a <see cref="RotarSiNecesarioAsync"/>.
    /// </summary>
    public RotadorRegistros(ConfiguracionRegistro configuracion)
        : this(configuracion.ResolverRuta(), configuracion.TamanoMaximoBytes, configuracion.FicherosConservados, tamanoFichero: null)
    {
    }

    /// <summary>
    /// Crea un rotador con piezas inyectadas. Usado en tests.
    /// </summary>
    /// <param name="rutaActiva">Ruta del fichero activo.</param>
    /// <param name="tamanoMaximoBytes">Tamano maximo antes de rotar. 0 desactiva la rotacion por tamano.</param>
    /// <param name="ficherosConservados">Numero de ficheros a conservar contando el activo. Se acota a >=1.</param>
    /// <param name="tamanoFichero">Funcion opcional que devuelve el tamano actual del fichero. Por defecto se usa <see cref="FileInfo.Length"/>.</param>
    public RotadorRegistros(
        string rutaActiva,
        long tamanoMaximoBytes,
        int ficherosConservados,
        Func<long>? tamanoFichero = null)
    {
        if (string.IsNullOrWhiteSpace(rutaActiva))
        {
            throw new ArgumentException("La ruta del fichero activo no puede estar vacia.", nameof(rutaActiva));
        }
        if (tamanoMaximoBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tamanoMaximoBytes), "Debe ser mayor o igual que 0.");
        }

        _rutaActiva = rutaActiva;
        _tamanoMaximoBytes = tamanoMaximoBytes;
        _ficherosConservados = Math.Max(1, ficherosConservados);
        _tamanoFichero = tamanoFichero;
    }

    /// <summary>
    /// Ruta del fichero activo.
    /// </summary>
    public string RutaActiva => _rutaActiva;

    /// <summary>
    /// Tamano maximo configurado antes de rotar.
    /// </summary>
    public long TamanoMaximoBytes => _tamanoMaximoBytes;

    /// <summary>
    /// Numero de ficheros que se conservan contando el activo.
    /// </summary>
    public int FicherosConservados => _ficherosConservados;

    /// <summary>
    /// Tamano actual del fichero activo. Devuelve 0 si no existe.
    /// </summary>
    public long TamanoActual()
    {
        if (_tamanoFichero is not null)
        {
            return _tamanoFichero();
        }
        return File.Exists(_rutaActiva) ? new FileInfo(_rutaActiva).Length : 0L;
    }

    /// <summary>
    /// Rota si el fichero activo supera el tamano maximo. Tras la rotacion
    /// el fichero activo se trunca a vacio (los siguientes mensajes empiezan
    /// un fichero nuevo). Si no supera el tamano, no hace nada.
    /// </summary>
    /// <returns>true si se ha rotado, false si no era necesario.</returns>
    public bool RotarSiEsNecesario()
    {
        lock (_candado)
        {
            if (_tamanoMaximoBytes <= 0)
            {
                return false;
            }
            if (!File.Exists(_rutaActiva))
            {
                return false;
            }
            if (TamanoActual() < _tamanoMaximoBytes)
            {
                return false;
            }

            Rotar();
            return true;
        }
    }

    /// <summary>
    /// Rota los ficheros a disco, desplazando <c>ruta.N-1</c> -> borrado,
    /// <c>ruta.N-2</c> -> <c>ruta.N-1</c>, ..., <c>ruta</c> -> <c>ruta.1</c>.
    /// El fichero activo queda vacio para empezar un ciclo nuevo.
    /// </summary>
    private void Rotar()
    {
        string directorio = Path.GetDirectoryName(_rutaActiva) ?? string.Empty;
        string nombre = Path.GetFileName(_rutaActiva);

        // Primero eliminamos el mas antiguo, si lo hay, para dejar sitio.
        string rutaMasAntigua = Path.Combine(directorio, $"{nombre}.{_ficherosConservados - 1}");
        if (File.Exists(rutaMasAntigua))
        {
            File.Delete(rutaMasAntigua);
        }

        // Desplazamos los rotados hacia arriba: ruta.N-2 -> ruta.N-1, ...
        for (int indice = _ficherosConservados - 2; indice >= 1; indice--)
        {
            string origen = Path.Combine(directorio, $"{nombre}.{indice}");
            string destino = Path.Combine(directorio, $"{nombre}.{indice + 1}");
            if (File.Exists(origen))
            {
                if (File.Exists(destino))
                {
                    File.Delete(destino);
                }
                File.Move(origen, destino);
            }
        }

        // El activo pasa a ruta.1.
        string primeraRotacion = Path.Combine(directorio, $"{nombre}.1");
        if (File.Exists(primeraRotacion))
        {
            File.Delete(primeraRotacion);
        }
        File.Move(_rutaActiva, primeraRotacion);

        // El activo debe volver a existir vacio para que la siguiente escritura
        // no tenga que crearlo de cero. Asi, ademas, el comportamiento observable
        // (siempre hay un fichero activo presente) coincide con lo que dice la doc.
        using (File.Create(_rutaActiva))
        {
        }

        // Nos aseguramos de que el directorio del activo exista (puede haber sido
        // borrado entre vuelta y vuelta por un operador humano).
        if (!string.IsNullOrEmpty(directorio) && !Directory.Exists(directorio))
        {
            Directory.CreateDirectory(directorio);
        }
    }
}