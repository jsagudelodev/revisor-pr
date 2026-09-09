using System;
using Microsoft.Extensions.Logging;

namespace RevisorPrs.Servicio;

public class EjecutorVuelta : IEjecutorVuelta
{
    private readonly ILogger<EjecutorVuelta> _logger;
    private readonly IClienteBitbucket _clienteBitbucket;
    private readonly DecisorRevisar _decisor;
    private readonly IRevisor _revisor;
    private readonly IAlmacen _almacen;
    private readonly ConfiguracionSondeo _configuracionSondeo;
    private readonly Func<DateTimeOffset> _ahora;
    private readonly EstadoServicio? _estado;
    private readonly SaneadorSecretos _saneador;
    private readonly RecortadorDiff? _recortador;
    private readonly FiltroRuido? _filtro;
    private readonly ConfiguracionLlm? _configuracionLlm;
    private readonly ConfiguracionBitbucket? _configuracionBitbucket;

    /// <param name="recortador">
    /// Acota el diff al tope configurado antes de enviarlo al modelo. Si es null,
    /// el diff viaja entero (comportamiento usado por los tests que no ejercitan el recorte).
    /// </param>
    /// <param name="filtro">
    /// Descarta los hallazgos que no merecen un comentario. Si es null, se publican
    /// todos (comportamiento usado por los tests que no ejercitan el filtrado).
    /// </param>
    public EjecutorVuelta(
        ILogger<EjecutorVuelta> logger,
        IClienteBitbucket clienteBitbucket,
        DecisorRevisar decisor,
        IRevisor revisor,
        IAlmacen almacen,
        ConfiguracionSondeo configuracionSondeo,
        Func<DateTimeOffset>? ahora = null,
        EstadoServicio? estado = null,
        SaneadorSecretos? saneador = null,
        RecortadorDiff? recortador = null,
        FiltroRuido? filtro = null,
        ConfiguracionLlm? configuracionLlm = null,
        ConfiguracionBitbucket? configuracionBitbucket = null)
    {
        _logger = logger;
        _clienteBitbucket = clienteBitbucket;
        _decisor = decisor;
        _revisor = revisor;
        _almacen = almacen;
        _configuracionSondeo = configuracionSondeo;
        _ahora = ahora ?? (() => DateTimeOffset.UtcNow);
        _estado = estado;
        _saneador = saneador ?? SaneadorSecretos.Ninguno;
        _recortador = recortador;
        _filtro = filtro;
        _configuracionLlm = configuracionLlm;
        _configuracionBitbucket = configuracionBitbucket;
    }

    public async Task EjecutarAsync(CancellationToken cancelacion)
    {
        _logger.LogInformation("Iniciando vuelta de sondeo.");

        int revisados = 0;
        int omitidos = 0;
        int fallidos = 0;

        var todosLosPrs = new List<PullRequest>();

        // La guia de convenciones se lee una vez por (repositorio, rama de destino) y no
        // una por pull request: varios PRs contra main comparten exactamente la misma.
        // Se descarta al terminar la vuelta, asi que editarla surte efecto en la siguiente.
        var guiasDeEsteSondeo = new Dictionary<(string, string), string?>();

        foreach (var repo in _configuracionSondeo.Repositorios)
        {
            try
            {
                var prs = await _clienteBitbucket.ListarPrsAbiertos(repo, cancelacion);
                todosLosPrs.AddRange(prs.Select(
                    p => new PullRequest(repo, p.Numero, p.Commit, p.Titulo, p.Descripcion, p.Rama)));
            }
            catch (OperationCanceledException) when (cancelacion.IsCancellationRequested)
            {
                // El servicio se está parando: abandonamos la vuelta entera en lugar de
                // recorrer el resto de repositorios registrando un error por cada uno.
                _logger.LogInformation("Vuelta de sondeo interrumpida al pararse el servicio.");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al listar PRs del repositorio {Repositorio}. Se continuará con el siguiente.", repo);

                // Tambien al estado: es el modo de fallo mas probable en produccion
                // (credenciales caducadas, repositorio renombrado) y el endpoint hecho
                // para diagnosticarlo lo ocultaba, devolviendo "ultimosErrores": [].
                _estado?.RegistrarError(
                    $"no se pudo listar {repo}: {_saneador.Sanear(ex.Message)}");
            }
        }
        
        var prsParaRevisar = _decisor.FiltrarPrsParaRevisar(todosLosPrs);

        // Anyadimos los PRs que el almacen tiene en backoff por un fallo
        // reciente: aunque el decisor ya los conozca con el mismo commit, el
        // ejecutor debe decidir si el plazo de reintento ha vencido.
        var prsParaRevisarLista = prsParaRevisar.ToList();
        foreach (var f in _almacen.ListarFallos())
        {
            if (f.Commit is null) continue;
            if (prsParaRevisarLista.Any(p => p.Repositorio == f.Repositorio && p.Numero == f.PullRequest))
            {
                continue;
            }
            prsParaRevisarLista.Add(new PullRequest(f.Repositorio, f.PullRequest, f.Commit));
        }
        prsParaRevisar = prsParaRevisarLista;

        foreach (var pr in prsParaRevisar)
        {
            var resultado = await ProcesarPrAsync(pr, guiasDeEsteSondeo, cancelacion);
            switch (resultado)
            {
                case ResultadoPr.Revisado: revisados++; break;
                case ResultadoPr.Omitido: omitidos++; break;
                case ResultadoPr.Fallido: fallidos++; break;
                case ResultadoPr.Interrumpido: return;
            }
        }

        if (_estado is not null)
        {
            _estado.RegistrarVuelta(new ResultadoVuelta
            {
                PrsRevisados = revisados,
                PrsOmitidos = omitidos,
                PrsFallidos = fallidos,
            });
        }

        _logger.LogInformation("Vuelta de sondeo finalizada.");
    }

    /// <summary>
    /// Deja constancia de lo que ha costado revisar un pull request.
    /// </summary>
    private void AnotarConsumo(PullRequest pr, ConsumoTokens consumo)
    {
        if (consumo.Total <= 0)
        {
            return;
        }

        _estado?.RegistrarConsumo(consumo);

        _logger.LogInformation(
            "PR {Repositorio}#{Numero} revisado con {Total} tokens ({Entrada} de entrada, {Salida} de salida).",
            pr.Repositorio, pr.Numero, consumo.Total, consumo.Entrada, consumo.Salida);
    }

    /// <summary>
    /// Como termino el procesamiento de un pull request.
    /// </summary>
    private enum ResultadoPr
    {
        Revisado,
        Omitido,
        Fallido,

        /// <summary>El servicio se esta parando: hay que abandonar la vuelta.</summary>
        Interrumpido,
    }

    /// <summary>
    /// Revisa UN pull request concreto, saltandose el sondeo.
    /// </summary>
    /// <remarks>
    /// Lo usa el disparo por webhook: cuando Bitbucket avisa de un empuje, no tiene
    /// sentido esperar a la siguiente vuelta ni volver a listar todos los repositorios.
    /// Las guardas de idempotencia son las mismas que en el sondeo, asi que un webhook y
    /// una vuelta que coincidan sobre el mismo PR no lo revisan dos veces.
    /// </remarks>
    public async Task RevisarPrAsync(PullRequest pr, CancellationToken cancelacion)
    {
        ArgumentNullException.ThrowIfNull(pr);

        _logger.LogInformation(
            "Revision inmediata de {Repositorio}#{Numero} (commit {Commit}).",
            pr.Repositorio, pr.Numero, pr.Commit);

        var resultado = await ProcesarPrAsync(
            pr, new Dictionary<(string, string), string?>(), cancelacion);

        _estado?.RegistrarVuelta(new ResultadoVuelta
        {
            PrsRevisados = resultado == ResultadoPr.Revisado ? 1 : 0,
            PrsOmitidos = resultado == ResultadoPr.Omitido ? 1 : 0,
            PrsFallidos = resultado == ResultadoPr.Fallido ? 1 : 0,
        });
    }

    /// <summary>
    /// Procesa un pull request de principio a fin: guardas, diff, revision y publicacion.
    /// </summary>
    /// <remarks>
    /// Vive aparte del bucle de sondeo porque el disparo por webhook necesita justo esto
    /// para un solo pull request. Que ambos caminos compartan este metodo es lo que
    /// garantiza que un PR revisado por webhook pase por las mismas guardas.
    /// </remarks>
    private async Task<ResultadoPr> ProcesarPrAsync(
        PullRequest pr,
        Dictionary<(string, string), string?> guias,
        CancellationToken cancelacion)
    {
        try
        {
            if (_almacen.Revisado(pr.Repositorio, pr.Numero, pr.Commit))
            {
                _logger.LogInformation(
                    "PR {Repositorio}#{Numero} commit {Commit} ya revisado. Saltando.",
                    pr.Repositorio, pr.Numero, pr.Commit);
                return ResultadoPr.Omitido;
            }

            if (!_almacen.DebeReintentar(pr.Repositorio, pr.Numero, _ahora()))
            {
                _logger.LogInformation(
                    "PR {Repositorio}#{Numero} en backoff por fallos previos. Se omite hasta el proximo reintento.",
                    pr.Repositorio, pr.Numero);
                return ResultadoPr.Omitido;
            }

            // Un fallo al descargar el diff sale por excepcion y lo recoge el catch de
            // abajo, que aplica backoff y deja el PR para la siguiente vuelta. Aqui, por
            // tanto, un diff vacio solo puede significar una cosa: el pull request no
            // trae cambios. No hay nada que preguntarle al modelo.
            var diffCompleto = await _clienteBitbucket.ObtenerDiff(pr.Repositorio, pr.Numero, cancelacion);

            if (string.IsNullOrWhiteSpace(diffCompleto))
            {
                _logger.LogInformation(
                    "PR {Repositorio}#{Numero} commit {Commit} no trae cambios. Se da por revisado sin llamar al modelo.",
                    pr.Repositorio, pr.Numero, pr.Commit);
                _almacen.MarcarRevisado(pr.Repositorio, pr.Numero, pr.Commit);
                return ResultadoPr.Revisado;
            }

            // El diff se acota ANTES de enviarlo al modelo. A partir de aqui se trabaja
            // siempre con el diff recortado: es lo unico que el modelo ha visto, asi que
            // tambien es la referencia contra la que el filtro comprueba si una linea
            // senalada existe de verdad.
            var diff = _recortador is null ? diffCompleto : _recortador.Recortar(diffCompleto);

            string? guia = await ObtenerGuiaAsync(pr, guias, cancelacion);

            // Al modelo se le manda el diff con cada linea numerada, para que no tenga
            // que deducir el numero contando desde las cabeceras. El filtro sigue
            // recibiendo el diff sin numerar: lo que valida son los rangos de las
            // cabeceras, que la numeracion no toca.
            var resultadoRevision = await _revisor.RevisarAsync(
                NumeradorDiff.Numerar(diff),
                new ContextoRevision(pr.Titulo, pr.Descripcion, guia),
                cancelacion);

            // El coste se anota SIEMPRE, saliera bien o mal: una llamada que no sirvio
            // se paga igual.
            AnotarConsumo(pr, resultadoRevision.Consumo);

            if (!resultadoRevision.Exito)
            {
                string motivo = _saneador.Sanear(resultadoRevision.Motivo ?? "revision sin exito");
                _logger.LogWarning(
                    "La revision del PR {Repositorio}#{Numero} no tuvo exito: {Motivo}",
                    pr.Repositorio, pr.Numero, motivo);
                _almacen.MarcarFallido(pr.Repositorio, pr.Numero, pr.Commit, motivo);
                _estado?.RegistrarError($"PR {pr.Repositorio}#{pr.Numero} sin exito: {motivo}");
                return ResultadoPr.Fallido;
            }

            var hallazgos = _filtro is null
                ? resultadoRevision.Hallazgos
                : _filtro.Filtrar(resultadoRevision.Hallazgos, diff);

            await ComunicarHallazgosAsync(pr, hallazgos, cancelacion);

            _almacen.MarcarRevisado(pr.Repositorio, pr.Numero, pr.Commit);
            return ResultadoPr.Revisado;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "Error procesando PR {Repositorio}#{Numero}.", pr.Repositorio, pr.Numero);

            // Si la operacion fue cancelada (caida del servicio, RV.17), no dejamos al PR
            // en backoff: queremos que la siguiente vuelta lo reintente sin penalizacion.
            if (ex is OperationCanceledException)
            {
                return cancelacion.IsCancellationRequested
                    ? ResultadoPr.Interrumpido
                    // OperationCanceledException sin cancelacion solicitada: cliente
                    // simulado (tests), seguimos con el siguiente.
                    : ResultadoPr.Omitido;
            }

            string detalle = _saneador.Sanear(ex.Message);
            _almacen.MarcarFallido(pr.Repositorio, pr.Numero, pr.Commit, detalle);
            _estado?.RegistrarError($"PR {pr.Repositorio}#{pr.Numero} exception: {detalle}");
            return ResultadoPr.Fallido;
        }
    }

    /// <summary>
    /// Tope de caracteres de la guia del equipo. Una guia larguisima desplazaria al
    /// propio diff dentro de la ventana del modelo, asi que se recorta.
    /// </summary>
    private const int TopeCaracteresGuia = 8000;

    /// <summary>
    /// Máximo de hallazgos que se comunican en una revisión. Muy por encima de lo que
    /// produce una revisión razonable: está para acotar el daño, no para filtrar ruido.
    /// </summary>
    private const int TopeHallazgosPorRevision = 50;

    /// <summary>
    /// Lee las convenciones que el equipo haya dejado en su repositorio.
    /// </summary>
    /// <remarks>
    /// Se lee de la RAMA DE DESTINO del pull request, no de la rama del PR. La diferencia
    /// importa: si se leyera del PR, cualquiera podria incluir en su propio pull request
    /// una guia que dijera "no reportes nada" y desactivar la revision que se le va a
    /// aplicar. En la rama de destino, cambiar la guia exige pasar por una revision.
    ///
    /// Que no haya guia es el caso normal, no un error: el revisor funciona sin ella.
    /// </remarks>
    private async Task<string?> ObtenerGuiaAsync(
        PullRequest pr,
        Dictionary<(string, string), string?> cache,
        CancellationToken cancelacion)
    {
        string ruta = _configuracionBitbucket?.RutaGuiaRepositorio ?? string.Empty;
        if (string.IsNullOrWhiteSpace(ruta) || string.IsNullOrWhiteSpace(pr.RamaDestino))
        {
            return null;
        }

        var clave = (pr.Repositorio, pr.RamaDestino!);
        if (cache.TryGetValue(clave, out string? guardada))
        {
            return guardada;
        }

        string? guia = await _clienteBitbucket.ObtenerArchivo(
            pr.Repositorio, pr.RamaDestino!, ruta, cancelacion);

        if (!string.IsNullOrWhiteSpace(guia) && guia!.Length > TopeCaracteresGuia)
        {
            _logger.LogWarning(
                "La guía {Ruta} de {Repositorio} ocupa {Caracteres} caracteres y se recorta a {Tope}.",
                ruta, pr.Repositorio, guia.Length, TopeCaracteresGuia);
            guia = guia.Substring(0, TopeCaracteresGuia);
        }

        if (!string.IsNullOrWhiteSpace(guia))
        {
            _logger.LogInformation(
                "Aplicando las convenciones de {Ruta} ({Repositorio}@{Rama}).",
                ruta, pr.Repositorio, pr.RamaDestino);
        }

        cache[clave] = guia;
        return guia;
    }

    /// <summary>
    /// Lleva los hallazgos al pull request: los graves anclados a su línea y el resto
    /// agrupados en un único comentario de resumen.
    /// </summary>
    /// <remarks>
    /// Un comentario por hallazgo hacía que un pull request con doce hallazgos generara
    /// doce notificaciones a cada persona suscrita, que es la forma más rápida de que un
    /// equipo silencie un revisor automático. El umbral de anclaje se configura en
    /// <c>Llm.SeveridadAnclada</c> y por defecto solo los errores interrumpen con
    /// comentario propio.
    ///
    /// Un hallazgo ya comentado no se repite, ni anclado ni dentro del resumen, así que
    /// un pull request sin novedades no recibe nada.
    /// </remarks>
    private async Task ComunicarHallazgosAsync(
        PullRequest pr,
        IReadOnlyList<Hallazgo> hallazgos,
        CancellationToken cancelacion)
    {
        int? umbralAnclaje = Severidades.Peso(_configuracionLlm?.SeveridadAnclada);

        // Tope duro de lo que puede acabar en el pull request. Una revisión honesta no
        // llega aquí: por encima de este número el modelo se ha ido del guion, y ese es
        // justo el resultado de una inyección lograda. Acota el daño sin depender de que
        // el modelo obedezca.
        var aComunicar = hallazgos;
        if (aComunicar.Count > TopeHallazgosPorRevision)
        {
            _logger.LogWarning(
                "La revisión de {Repositorio}#{Numero} devolvió {Cantidad} hallazgos, por encima del tope de {Tope}. Se publican los primeros.",
                pr.Repositorio, pr.Numero, aComunicar.Count, TopeHallazgosPorRevision);
            aComunicar = aComunicar.Take(TopeHallazgosPorRevision).ToList();
        }

        var paraResumen = new List<Hallazgo>();
        int anclados = 0;

        foreach (var hallazgo in aComunicar)
        {
            // El pull request no se da por revisado hasta el final, así que una caída a
            // mitad hace que la vuelta siguiente vuelva a pasar por aquí. El registro
            // por hallazgo evita que el autor vea el mismo comentario dos veces.
            if (_almacen.ComentarioPublicado(pr.Repositorio, pr.Numero, hallazgo.Huella()))
            {
                _logger.LogInformation(
                    "Hallazgo ya comentado en {Repositorio}#{Numero} ({Archivo}:{Linea}). No se repite.",
                    pr.Repositorio, pr.Numero, hallazgo.Archivo, hallazgo.Linea?.ToString() ?? "(sin línea)");
                continue;
            }

            if (SeAncla(hallazgo, umbralAnclaje))
            {
                await _clienteBitbucket.PublicarComentario(pr.Repositorio, pr.Numero, hallazgo, cancelacion);

                // Se anota DESPUÉS de publicar: si el orden fuera el inverso, una caída
                // entre ambos dejaría el hallazgo marcado sin haber llegado nunca al PR,
                // y se perdería en silencio. Al revés, lo peor que puede pasar es
                // repetir un único comentario.
                _almacen.MarcarComentarioPublicado(
                    pr.Repositorio, pr.Numero, pr.Commit, hallazgo.Huella(), hallazgo.Resumen);
                anclados++;
            }
            else
            {
                paraResumen.Add(hallazgo);
            }
        }

        if (paraResumen.Count == 0)
        {
            return;
        }

        string resumen = FormateadorComentario.ComponerResumen(paraResumen, anclados);
        await _clienteBitbucket.PublicarComentarioGeneral(pr.Repositorio, pr.Numero, resumen, cancelacion);

        foreach (var hallazgo in paraResumen)
        {
            _almacen.MarcarComentarioPublicado(
                pr.Repositorio, pr.Numero, pr.Commit, hallazgo.Huella(), hallazgo.Resumen);
        }

        _logger.LogInformation(
            "PR {Repositorio}#{Numero}: {Anclados} hallazgo(s) anclado(s) y {EnResumen} en el comentario de resumen.",
            pr.Repositorio, pr.Numero, anclados, paraResumen.Count);
    }

    /// <summary>
    /// Un hallazgo se ancla si alcanza el umbral de severidad Y se puede anclar: sin
    /// archivo o sin línea Bitbucket no tiene dónde ponerlo, así que va al resumen.
    /// </summary>
    private static bool SeAncla(Hallazgo hallazgo, int? umbralAnclaje)
    {
        if (string.IsNullOrWhiteSpace(hallazgo.Archivo) || !hallazgo.Linea.HasValue)
        {
            return false;
        }

        if (!umbralAnclaje.HasValue)
        {
            // Umbral vacío o no reconocido: se ancla todo, como antes del resumen.
            return true;
        }

        return (Severidades.Peso(hallazgo.Severidad) ?? -1) >= umbralAnclaje.Value;
    }
}
