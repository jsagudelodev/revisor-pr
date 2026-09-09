RV.3 completado: Implementado tipo EventoPr y mapper EventoPrMapper con tests que verifican mapeo correcto y descarte seguro.
RV.5: Se implementó ObtenerDiff y sus tests; la ejecución real contra un PR permanece pendiente por falta de app password.
RV.14b: Se verifica la idempotencia de la vuelta de sondeo con dos tests (dos vueltas no publican dos veces; commit nuevo sobre el mismo PR sí se revisa) y se hace que AlmacenFalso respete MarcarRevisado para ejercitar la guarda.
--- Sesión 2026-09-09: de prototipo a revisor usable ---

Arreglos previos al backlog (5): el servicio no arrancaba (Almacen pedía un string que
el contenedor no resolvía); FiltroRuido y RecortadorDiff estaban construidos y probados
pero sin cablear; ObtenerDiff devolvía vacío ante error y marcaba el PR como revisado
para siempre; CancellationToken.None fijo en todo el cliente; e higiene (backoff sin
limpiar, proximaVueltaUtc nulo, BuildServiceProvider paralelo, saneador que mutilaba el
log, AddWindowsService ausente).

C1 completado: flujo de integración continua en .github/workflows/ci.yml. Compila en
Release con -warnaserror y ejecuta la suite en cada push y PR contra main, publicando
los resultados en TRX. Los comandos exactos se verificaron en local antes de subirlos.

A2 completado: un comentario no se publica dos veces en el mismo pull request.
- Hallazgo.Huella() da identidad estable a partir de archivo+línea+severidad. Deja fuera
  la prosa del modelo a propósito: al reintentar se vuelve a llamar al LLM sobre el mismo
  diff y basta una reformulación para que una huella basada en texto falle justo en el
  caso que debe cubrir.
- Migración 5: columna Huella en HallazgosPublicados (tabla que existía desde la
  migración 2 sin que nadie la escribiera) más índice ÚNICO por (Repositorio,
  PullRequest, Huella).
- IAlmacen gana ComentarioPublicado y MarcarComentarioPublicado. El ejecutor consulta
  antes de publicar y anota DESPUÉS: al revés, una caída entre ambos perdería el
  hallazgo en silencio, y repetir un comentario es menos grave que perderlo.
- PublicarComentario pasa a propagar los fallos en vez de tragárselos, porque el
  ejecutor anota como publicado en cuanto la llamada vuelve.
- Decisión de producto: la huella no incluye el commit, así que un hallazgo que sigue
  vigente tras un empuje nuevo NO se re-comenta. El test
  EjecutarAsync_CommitNuevoSobreMismoPr_SiSeRevisa afirmaba lo contrario y se reescribió
  para comprobar lo que de verdad importa (que el commit nuevo sí se revisa), más un
  caso nuevo para un hallazgo distinto, que sí se comenta.

De camino: migraciones dentro de transacción (D4) y liberación de las respuestas HTTP
que faltaban en ListarPrsAbiertos y PublicarComentario (D2).

Suite: 143 pruebas en verde. Se comprobó que los tests nuevos fallan al revertir cada
arreglo (2 fallos al desactivar la guarda de idempotencia).

A1 completado: el comentario del pull request lleva ya severidad, resumen y detalle.
- FormateadorComentario compone el cuerpo en Markdown. Vive fuera de ClienteBitbucket
  porque qué lee el equipo es decisión de producto, no de transporte, y así se prueba
  sin levantar HTTP.
- Severidades centraliza los dos vocabularios (error/warning/info del modelo y
  baja/media/alta de la configuración) y da la etiqueta que se muestra. FiltroRuido pasa
  a usarlo en vez de mantener su propia tabla, que era la misma duplicada.
- Formato anclado:  "**Error** · <resumen>" + detalle debajo. No repite archivo ni línea
  porque Bitbucket ya los muestra junto al comentario anclado.
- Formato general:  "**Aviso** · `archivo:linea` · <resumen>" + detalle.
- Decisiones: etiquetas de texto en negrita y no emoji, para que rendericen igual en
  cualquier cliente; sin firma al pie, porque Bitbucket ya muestra el autor y repetir
  "revisión automática" en cada comentario sería ruido en un hilo largo; y el detalle se
  omite si está vacío o si repite el resumen.
- De paso desaparece el ":0" que se publicaba cuando el hallazgo no traía línea.

Suite: 157 pruebas en verde. Desconectar el formateador hace fallar 2 tests.

A4 completado: los archivos que nadie revisa a mano ya no se mandan al modelo.
- ExclusionRutas empareja rutas con globs (* dentro de un segmento, ** entre segmentos,
  ? un caracter). Un patron SIN barras se compara contra el nombre del archivo, para que
  "*.min.js" valga a cualquier profundidad sin escribir "**/". Sin distinguir mayusculas.
- Lista de serie conservadora: ficheros de bloqueo (package-lock, yarn.lock, Cargo.lock,
  Gemfile.lock, poetry.lock, composer.lock, packages.lock.json), node_modules, vendor,
  dist, bin, obj, minificados, mapas de origen, generados (.designer.cs, .g.cs, .pb.go,
  _pb2.py) e instantaneas .snap. Las migraciones de base de datos NO se excluyen: son
  generadas, pero esconderlas taparia problemas reales.
- La exclusion se aplica ANTES del tope de bytes. Era lo importante: un fichero de
  bloqueo de 2 MB se comia el presupuesto y dejaba fuera el codigo que si importa.
- Si todo queda excluido, Recortar devuelve vacio y el ejecutor da el PR por revisado
  sin gastar una llamada al modelo. Compone con el arreglo del diff vacio.
- Los excluidos van al log, no dentro del prompt: son politica nuestra, no informacion
  que el modelo necesite, y meterlos en el diff solo gastaria tokens.
- Configurable en Bitbucket:RutasExcluidas. Omitirlo usa los de serie; una lista vacia
  desactiva la exclusion. Documentado en appsettings.example.json.
- Alcance recortado a proposito: los patrones son globales, no por repositorio. Que cada
  equipo ajuste los suyos encaja mejor en B3 (guia dentro del propio repositorio), donde
  lo edita el equipo y no quien opera el servicio.

Suite: 193 pruebas en verde. Desactivar la lista de serie hace fallar 4 tests.

A5 completado: la linea de un hallazgo se comprueba contra los hunks DE SU ARCHIVO.
- MapaDeTramos sustituye a la lista global. Lee las cabeceras "diff --git", "---" y
  "+++" para atribuir cada hunk a su archivo. El tramo "+" va al archivo nuevo y el "-"
  al viejo, asi que un renombrado acepta ambas rutas con sus numeraciones respectivas.
- Normalizacion de rutas: quita prefijos a/ y b/, barra inicial, unifica separadores y
  corta la marca que git anade tras un tabulador. "/dev/null" se traduce a "sin ruta",
  de modo que un archivo recien creado conserva su ruta nueva.
- Tolerancia con lo que escriba el modelo: si no encuentra la ruta exacta, prueba por
  nombre de archivo; si hay dos archivos con el mismo nombre en carpetas distintas, NO
  adivina y descarta.
- Un diff sin cabeceras de archivo (malformado) cae en una bolsa que vale para cualquier
  archivo: ante un diff que no sabemos leer es preferible no descartar nada a tirarlo
  todo. Los diffs reales de Bitbucket siempre traen cabeceras.
- Un hallazgo sin linea sigue siendo comentario general y esta regla no lo juzga.
- El DiffEjemplo de FiltroRuidoTests no tenia cabeceras de archivo, asi que los tests
  antiguos no ejercitaban nada de esto: se hizo realista con tres secciones.

Suite: 206 pruebas en verde. Volver a los tramos globales hace fallar 4 tests.

Estado del backlog: 5 items completados (C1, A2, A1, A4, A5) mas D2 y D4 de propina.
En la banda P0 quedan 4 de 5 hechos; sigue abierto A3 (comentario de resumen con tope
por pull request).

A3 completado: comentario de resumen, con la banda P0 del backlog ya cerrada.
- Decision del usuario (se le consulto antes de escribir codigo): solo los hallazgos de
  severidad Error se anclan a su linea; el resto se agrupa en un unico comentario de
  resumen. Y el resumen solo se publica si hay algo que resumir, de modo que un pull
  request limpio no recibe nada y el silencio significa "sin problemas".
- Un PR con 2 errores, 6 avisos y 4 notas pasa de 12 notificaciones a 3.
- FormateadorComentario.ComponerResumen reutiliza Componer() para cada entrada, asi que
  el detalle con la sugerencia de arreglo se conserva tambien en el resumen: agrupar no
  puede costar lo que se gano en A1.
- Cabecera con el recuento por severidad de mayor a menor, y mencion de cuantos van
  anclados solo si los hay.
- IClienteBitbucket gana PublicarComentarioGeneral: el resumen no describe un hallazgo,
  asi que no puede pasar por PublicarComentario. Ambos comparten EnviarComentarioAsync.
- La idempotencia de A2 cubre tambien el resumen: los hallazgos listados se marcan como
  publicados, asi que una vuelta posterior sin novedades no publica un segundo resumen.
- Un hallazgo grave SIN linea va al resumen: Bitbucket no tiene donde anclarlo.
- Unico ajuste nuevo: Llm.SeveridadAnclada, por defecto "error". Con "baja" se ancla
  todo, que es el comportamiento anterior.

Dos correcciones durante el trabajo:
- ContarPorSeveridad ordenaba por el peso de la ETIQUETA ("Nota"), que no esta en la
  tabla de pesos, asi que todo empataba y salia "2 notas y 2 avisos". Ahora el peso se
  guarda al contar, tomado de la severidad original.
- Al verificar se vio que cambiar el VALOR POR DEFECTO de SeveridadAnclada no rompia
  ningun test, porque todos lo fijaban a mano. Como la promesa es que funcione recien
  instalado, se anadio un test que ejercita la configuracion sin tocar.

Suite: 218 pruebas en verde.

Estado del backlog: 6 items completados (C1, A2, A1, A4, A5, A3) mas D2 y D4. La banda
P0 queda cerrada; lo siguiente es P1 (calidad de la revision), empezando por B1 y B2.

B1 completado: el diff llega al modelo con cada linea numerada.
- NumeradorDiff antepone a cada linea de contenido su numero. Contexto y anadidas llevan
  el numero del archivo NUEVO; las eliminadas, el del ORIGINAL. Es la misma convencion
  que usa FiltroRuido al validar, asi que lo que el modelo senala y lo que aceptamos
  hablan del mismo sistema de numeracion.
- Las cabeceras pasan intactas. Importante: "---" y "+++" empiezan por las mismas marcas
  que el contenido, asi que se comprueban ANTES que la marca; numerarlas descuadraria
  los contadores del archivo entero.
- El prompt pasa de "NO inventes numeros de linea, usa null si no puedes determinarlos"
  a "copia EXACTAMENTE el numero que precede a la linea". Se le quita al modelo un
  trabajo determinista que hacia mal y que generaba parte del ruido que el filtro tenia
  que descartar despues.
- El filtro sigue recibiendo el diff SIN numerar: lo que valida son los rangos de las
  cabeceras, que la numeracion no toca.
- Verificado a mano sobre un diff con contexto, borrados y varios hunks: los contadores
  viejo y nuevo divergen correctamente.

Hueco detectado al verificar: desconectar el numerador del ejecutor NO rompia ningun
test. Es la misma clase de fallo que abrio esta sesion (FiltroRuido y RecortadorDiff
construidos y probados pero sin cablear), asi que se anadio una prueba del CABLEADO,
no solo de la numeracion.

Suite: 238 pruebas en verde.

B2 completado: el modelo ya no trabaja a ciegas sobre el diff.
Dos mitades:

1. INTENCION DEL PULL REQUEST.
   - EventoPr gana Descripcion (opcional: muchos equipos la dejan vacia, asi que su
     ausencia no puede invalidar el evento) y TraductorEventoPr la extrae.
   - PullRequest arrastra Titulo y Descripcion, que antes se perdian al convertir de
     EventoPr: el ejecutor construia PullRequest(repo, numero, commit) y tiraba el resto.
   - ContextoRevision se pasa a IRevisor.RevisarAsync. Es un tipo propio y no parametros
     sueltos porque va a crecer: la guia por repositorio (B3) entra ahi sin volver a
     tocar la firma ni los siete dobles de prueba.
   - El prompt de sistema pide usar la intencion para juzgar mejor, avisando de que NO
     sirve para callar un problema real.

2. CONTEXTO ALREDEDOR DEL CAMBIO.
   - Se pide con el parametro "context" de la propia API del diff de Bitbucket, igual que
     "git diff -U<n>". Descartada la alternativa de descargar los ficheros completos: una
     llamada extra por archivo y por vuelta, y habria que recortarlos a mano.
   - Bitbucket.LineasDeContexto, por defecto 10 (git usa 3, que se queda corto para
     entender una funcion). Sube el tamano del diff, asi que interactua con TopeBytesDiff.
   - Efecto util y buscado: las cabeceras @@ se ensanchan, asi que FiltroRuido acepta
     hallazgos sobre las lineas de contexto sin tocar nada.

Seguridad: el titulo y la descripcion los escribe quien abre el PR, asi que se anade
texto NO confiable al prompt. Se delimita el material del autor con marcas de bloque y
el prompt de sistema avisa de que son datos a analizar, no instrucciones. NO es la
defensa completa contra inyeccion: B4 sigue abierto.

Suite: 248 pruebas en verde. Revertir cada una de las tres piezas (paso de la intencion,
parametro context, extraccion de la descripcion) hace fallar su test.

B3 completado: cada equipo ajusta el revisor desde su propio repositorio.
- Fichero .revisorpr.md en la raiz del repositorio revisado (ruta configurable en
  Bitbucket.RutaGuiaRepositorio; vacia lo desactiva). Su contenido entra en el prompt
  como criterio de revision de ese equipo.
- DECISION DE SEGURIDAD, la mas importante del item: la guia se lee de la RAMA DE DESTINO
  del pull request, no de la rama del PR. Si se leyera del PR, cualquiera podria incluir
  en su propio pull request una guia que dijera "no reportes nada" y desactivar la
  revision que se le va a aplicar. En la rama de destino, cambiarla exige pasar por una
  revision del equipo. Hay un test dedicado y se comprobo que falla si se lee del commit.
- Por eso mismo la guia va FUERA de las marcas de material a revisar: a diferencia del
  titulo, la descripcion y el diff, no la escribe el autor del PR.
- Se lee una vez por (repositorio, rama de destino) y no una por pull request: varios PRs
  contra main comparten la misma. La cache dura solo la vuelta, asi que editar la guia
  surte efecto en el sondeo siguiente.
- Tope de 8000 caracteres: una guia sin limite desplazaria al propio diff dentro de la
  ventana del modelo.
- Que no haya guia es el caso normal, no un error: un 404 devuelve null sin lanzar.
- IClienteBitbucket.ObtenerArchivo lleva implementacion por defecto que devuelve null.
  Es capacidad opcional y evita obligar a los diez dobles de prueba a implementarla.
- El prompt de sistema dice que la guia tiene prioridad sobre las preferencias generales
  de estilo, pero solo sobre lo que menciona: lo demas se juzga con el criterio habitual.

Con esto queda absorbido el "por repositorio" que se recorto del alcance de A4.

Suite: 259 pruebas en verde.

Estado del backlog: 9 items completados. P0 entera y 3 de 4 de P1. Queda B4 en P1.

B4 completado: resistencia a instrucciones inyectadas desde el pull request.
Cuatro capas, ordenadas de menos a mas fiables:

1. DELIMITACION (la mas debil, depende de que el modelo obedezca). Ya existia de B2/B3.
   Se anade Neutralizar(): se quitan las marcas de bloque del titulo, la descripcion y el
   diff. Era el ataque evidente contra nuestro propio esquema: colar la marca de cierre y
   escribir despues, donde el modelo creeria que hablamos nosotros. Sin distinguir
   mayusculas, porque escribirla en minusculas no puede valer.

2. EL HALLAZGO TIENE QUE HABLAR DE UN ARCHIVO DEL DIFF. Regla nueva en FiltroRuido
   ("archivo fuera del diff"). No depende de que el modelo obedezca: si una inyeccion
   logra que hable de otro sitio, no llega al PR. Con un diff que no supimos atribuir se
   conserva todo, igual que el resto del filtro.

3. LA SEVERIDAD SE REDUCE AL JUEGO CONOCIDO. Severidades.Etiqueta devolvia el texto del
   modelo tal cual "para no perder informacion"; se revirtio esa decision. Esa etiqueta
   se publica en negrita dentro de un comentario Markdown del pull request, asi que
   devolver texto arbitrario es dejar que el contenido del PR escriba en la cabecera.

4. TOPES DE LO PUBLICABLE. Resumen a 300 caracteres, detalle a 2000, y 50 hallazgos por
   revision. Una revision honesta no se acerca a esos numeros: estan para acotar el dano,
   no para filtrar ruido.

Dos tests anteriores afirmaban lo contrario de lo que ahora hacemos y se reescribieron
explicando el cambio, no se ajustaron en silencio:
- Componer_SeveridadDesconocida_SeMuestraTalCual -> _PasaANota.
- SinLinea_SeConservaAunqueElArchivoNoEsteEnElDiff -> se parte en dos: se conserva si el
  archivo SI esta en el diff, se descarta si no.

Matiz honesto: UnHallazgoSobreOtroArchivo_SeDescarta no aisla la regla nueva, porque la
regla de linea (A5) ya atrapaba ese caso. Quien la aisla es el test de hallazgo SIN
linea, que escapa a la regla de linea. Queda anotado en el propio test.

Suite: 268 pruebas en verde. Desactivar las cuatro capas hace fallar 9 tests.

Estado del backlog: 10 items completados. P0 y P1 enteras. Quedan P2 (operacion) y P3
(deuda tecnica).

C2 completado: revision inmediata por webhook, con el sondeo como red de seguridad.

Tres piezas:
1. REFACTOR DEL EJECUTOR. El cuerpo del bucle de sondeo se extrae a ProcesarPrAsync y se
   anade RevisarPrAsync a IEjecutorVuelta. Que ambos caminos compartan ese metodo es lo
   que garantiza que un PR revisado por aviso pase por las MISMAS guardas de idempotencia
   que uno revisado por sondeo: un aviso y una vuelta que coincidan no lo revisan dos veces.
2. ColaDeRevisiones, canal acotado a 200. Existe para que la revision NO ocurra en el hilo
   que atiende la peticion HTTP: Bitbucket espera respuesta rapida y una revision tarda lo
   que tarde el modelo. Se responde 202 en cuanto esta encolado.
3. ServidorWebhook, servicio alojado con TcpListener, igual enfoque que ServidorEstado
   pero leyendo cuerpo. No se reutilizo ServidorEstado a proposito: tienen posturas de
   seguridad opuestas (loopback obligatorio frente a expuesto deliberadamente) y mezclarlas
   habria sido peor.

Decisiones de seguridad:
- Apagado de fabrica. Habilitado sin Secreto -> el servicio NO arranca. Un endpoint que
  dispara gasto en el modelo no puede quedar sin autenticar.
- Direccion loopback por defecto: exponerlo es una decision consciente, y se registra un
  aviso en el log si se escucha fuera de loopback.
- Dos formas de secreto (HMAC-SHA256 del cuerpo o Authorization: Bearer) porque no todas
  las instalaciones de Bitbucket ofrecen firma. La firma cubre el cuerpo, asi que alterarlo
  por el camino invalida el aviso.
- Comparaciones en tiempo constante: comparar secretos con igualdad normal filtra por el
  tiempo de respuesta cuantos caracteres iniciales se acertaron.
- PENDIENTE DE CONFIRMAR: el nombre y formato de la cabecera de firma dependen de la
  version de Bitbucket. Es configurable (Webhook.CabeceraFirma) y esta advertido en el
  README; hay que verificarlo contra la instalacion real antes de fiarse del camino HMAC.

Los avisos se atienden desde el MISMO hilo que el sondeo y bajo el mismo candado: el
almacen es una unica conexion SQLite y el decisor guarda estado en memoria, asi que
revisar en paralelo desde el hilo del webhook seria pedir una carrera de datos.

Correccion durante el trabajo: la cola usaba BoundedChannelFullMode.DropWrite, con el que
TryWrite devuelve true y descarta en silencio, justo lo contrario de lo que documentaba
Encolar. Se cambio a Wait, con el que TryWrite rechaza de verdad sin bloquear, para que
quien atiende el webhook sepa que no se encolo. Tambien se alineo la validacion del puerto
con la de Estado, que ya admitia 0 para pruebas.

Se anadio InternalsVisibleTo hacia RevisorPrs.Tests para probar la autenticacion y el
troceo HTTP sin exponerlos en la API publica.

Verificado con el servicio en marcha: aviso firmado -> 202 encolado; sin credencial -> 401.

Suite: 300 pruebas en verde, incluidas 8 que levantan el servidor y le hablan por HTTP.

Estado del backlog: 11 items completados. P0 y P1 enteras, y C1 y C2 de P2.

C3 completado: coste visible por pull request.
- ConsumoTokens (readonly record struct) con Entrada, Salida y suma. Se mide en TOKENS y
  no en dinero porque los tokens los devuelve el proveedor y no caducan; los precios
  cambian y varian por modelo, asi que codificarlos en el servicio seria garantizar que
  envejecen mal.
- Revisor lee el bloque "usage" de la respuesta. Es opcional a proposito: un proveedor
  que no lo devuelva no puede tumbar la revision. Quedarse sin la cifra es peor que
  tenerla, pero mucho mejor que fallar.
- El reintento SUMA. Una revision puede costar dos llamadas; contar solo una mentiria
  justo en el caso que mas cuesta.
- El coste de una revision FALLIDA tambien se cuenta: se paga igual, y ocultarlo falsearia
  la cifra justo cuando mas interesa mirarla.
- EstadoServicio acumula total, ultima revision y cuantas revisiones se han podido medir.
  Un consumo vacio no infla el contador de medidas.
- /estado publica un bloque "consumo". La estimacion en dinero solo aparece si el equipo
  configura Llm.CostePorMillonEntrada y CostePorMillonSalida; sin tarifas sale nula, no
  cero, para no confundir "no lo se" con "no cuesta nada".
- Cada revision deja ademas una linea en el log con sus tokens.

Suite: 314 pruebas en verde. Desactivar la anotacion en el ejecutor hace fallar 1 test;
desactivar la lectura de "usage" hace fallar 3.

Estado del backlog: 12 items completados. P0 y P1 enteras; de P2 quedan C4 y C5.

Pasada final: C4, C5 y la deuda de P3 (D1, D3, D5, D6, D7) de una vez.

C4: los fallos al listar un repositorio llegan ya a EstadoServicio, saneados. Era el modo
de fallo mas probable en produccion (credenciales caducadas) y el endpoint hecho para
diagnosticarlo devolvia "ultimosErrores": []. Verificado en vivo: ahora aparece
"no se pudo listar ws/repo: Respuesta no exitosa...".

C5: dos de tres.
- IsEnabled devolvia un suelo fijo en Information que ignoraba Logging:LogLevel, asi que
  el operador no podia bajar el nivel para diagnosticar ni subirlo para callar las trazas
  de HttpClient. Ahora solo rechaza None y deja filtrar al ILoggerFactory, que es quien
  aplica la configuracion. Verificado con LogLevel=Warning: desaparecen las trazas.
- Se quita el eco a Console.Out: el host ya trae su proveedor de consola, asi que cada
  linea salia dos veces. Verificado: de 2 apariciones a 1.
- CAMBIO DE OPINION sobre la tercera parte. Sustitui File.AppendAllText por un
  StreamWriter abierto para ahorrar aperturas, y lo revert al ver fallar dos tests: en
  Windows, un fichero abierto para escritura no lo puede leer una herramienta que abra con
  FileShare.Read (Bloc de notas, "type", File.ReadAllText). Es decir, el operador no podria
  leer el log mientras el servicio corre, que es justo cuando lo necesita. La mejora de
  rendimiento era teorica —unas lineas por vuelta— y el coste era una regresion de uso
  real. Queda documentado en el codigo para que nadie lo "optimice" otra vez, con un test
  que lo fija.

D1: DecisorRevisar ya no aplaza los PRs completamente nuevos cuando en la misma vuelta hay
otro con commit nuevo. Ese aplazamiento no evitaba ningun problema —las guardas del
almacen ya impiden revisar dos veces— y hacia esperar un intervalo entero. El test que
exigia lo contrario se reescribio explicando el cambio.

D3: borrado el appsettings.json de la raiz. Estaba fuera del proyecto, no se cargaba nunca
y usaba Llm:ApiKey donde el codigo lee ClaveApi: una trampa para quien lo editara.

D5: IntentarRecuperarJsonTruncado pasa de recorrer de derecha a izquierda probando seis
sufijos fijos y reparseando el prefijo entero en cada punto (coste cuadratico) a un solo
recorrido de izquierda a derecha con pila de llaves y corchetes, respetando cadenas y
escapes. Ademas de rapido es mas correcto: el cierre sale de la pila, no de adivinar
combinaciones. Con 4000 hallazgos y la respuesta cortada, se recuperan los 4000 en menos
de tres segundos.

D6: el motivo del truncado se conserva tambien en el camino del reintento. Antes se
descartaba, ocultando diagnostico en el caso mas raro.

D7: documentado donde poner las credenciales. dotnet user-secrets en desarrollo
(el proyecto ya traia UserSecretsId sin usar) y variables de entorno en produccion.

Suite: 328 pruebas en verde. Compilacion Release con -warnaserror limpia.

Estado del backlog: los 21 items completados. P0 (A1-A5), P1 (B1-B4), P2 (C1-C5) y
P3 (D1-D7). D2 y D4 se habian cerrado de camino al hacer A2. El backlog esta terminado.

Recorrido de la sesion: el servicio no arrancaba y tenia 107 pruebas; ahora arranca,
esta cableado, no repite comentarios, no comenta ruido, explica cada hallazgo, entiende
la intencion del PR, se ajusta por repositorio, resiste que le manipulen, responde en
segundos por webhook, ensena lo que cuesta y se puede diagnosticar. 328 pruebas.
