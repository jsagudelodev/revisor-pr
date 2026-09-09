using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace RevisorPrs.Servicio;

/// <summary>
/// Cola de pull requests que hay que revisar cuanto antes, sin esperar al sondeo.
/// </summary>
/// <remarks>
/// Es el punto de encuentro entre quien recibe el aviso (el webhook) y quien revisa (el
/// sondeo). Existe para que el revisor NO se ejecute en el hilo que atiende la petición
/// HTTP: Bitbucket espera una respuesta rápida, y una revisión tarda lo que tarde el
/// modelo. El webhook encola y responde; el sondeo consume.
///
/// La cola está acotada a propósito. Si se llena —una avalancha de empujes, o el modelo
/// atascado— es preferible descartar el aviso y dejar que el sondeo recoja ese pull
/// request en la vuelta siguiente, antes que acumular trabajo sin límite en memoria.
/// </remarks>
public sealed class ColaDeRevisiones
{
    /// <summary>
    /// Capacidad de la cola. Generosa para un pico normal de empujes, pequeña como para
    /// que una avalancha se note enseguida en lugar de comerse la memoria.
    /// </summary>
    public const int Capacidad = 200;

    private readonly Channel<PullRequest> _canal =
        Channel.CreateBounded<PullRequest>(new BoundedChannelOptions(Capacidad)
        {
            // Con "Wait", TryWrite devuelve false cuando la cola está llena en lugar de
            // aceptar y descartar en silencio, que es lo que hace DropWrite. Importa
            // porque quien atiende el webhook necesita SABER que no se encoló, para
            // registrarlo y contestar en consecuencia. TryWrite no bloquea nunca.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    /// <summary>
    /// Encola un pull request para revisión inmediata. Devuelve <c>false</c> si la cola
    /// está llena, en cuyo caso el sondeo se encargará más adelante.
    /// </summary>
    public bool Encolar(PullRequest pr) => _canal.Writer.TryWrite(pr);

    /// <summary>
    /// Espera al siguiente pull request encolado.
    /// </summary>
    public ValueTask<PullRequest> EsperarSiguienteAsync(CancellationToken cancelacion)
        => _canal.Reader.ReadAsync(cancelacion);

    /// <summary>
    /// Saca un pull request si hay alguno esperando, sin bloquear.
    /// </summary>
    public bool IntentarSacar(out PullRequest? pr)
    {
        bool hay = _canal.Reader.TryRead(out PullRequest? leido);
        pr = leido;
        return hay;
    }

    /// <summary>Cuántos pull requests esperan turno.</summary>
    public int Pendientes => _canal.Reader.Count;
}
