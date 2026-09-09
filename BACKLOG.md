# Backlog

De prototipo que no arrancaba a revisor que un equipo puede dejar encendido.

**Estado: 21 completados, 4 pendientes.** La suite pasó de 107 a 328 pruebas.

> **La herramienta todavía no ha revisado ningún pull request real.** Todo lo cerrado
> aquí es maquinaria: que arranque, que no repita, que no comente ruido, que resista que
> la manipulen, que enseñe lo que gasta. La calidad de los hallazgos —que es de lo que
> depende que el equipo la use— sigue sin medir. Ver [Pendiente](#pendiente).

El detalle de cada decisión, incluidos los cambios de opinión a mitad de camino, está
en [`docs/bitacora.md`](docs/bitacora.md).

| Banda | Qué cubre | Estado |
|---|---|---|
| [P0](#p0--bloquea-la-adopción) | Ruido que el equipo ve en sus pull requests | 5 / 5 |
| [P1](#p1--calidad-de-la-revisión) | Que además acierte | 4 / 4 |
| [P2](#p2--operación) | Mantenerlo encendido y saber qué hace | 5 / 5 |
| [P3](#p3--deuda-técnica) | Trampas para quien toque el código después | 7 / 7 |
| [Pendiente](#pendiente) | Saber si revisa bien, y poner freno al gasto | 0 / 4 |

Antes de empezar el backlog hubo cinco defectos que impedían usarlo:

- **El servicio no arrancaba.** `Almacen` pedía un `string` que el contenedor no sabía
  resolver; reventaba en `builder.Build()`.
- **Filtro y recortador huérfanos.** `FiltroRuido` y `RecortadorDiff` estaban construidos
  y probados, pero nadie los llamaba: `SeveridadMinima` y `TopeBytesDiff` no hacían nada.
- **Un 500 perdía la revisión para siempre.** `ObtenerDiff` devolvía cadena vacía ante
  cualquier error y el PR quedaba marcado como revisado sin hallazgos.
- **Las llamadas no se enteraban del apagado.** `CancellationToken.None` fijo en todo el
  cliente de Bitbucket.
- **Higiene:** backoff que no se limpiaba tras un éxito, `proximaVueltaUtc` siempre nulo,
  contenedor duplicado del log, saneador que mutilaba el log, y el servicio de Windows
  que no se registraba como tal.

---

## P0 — Bloquea la adopción

Un revisor automático se gana el sitio o se silencia en la primera semana.

### A1 · Publicar el detalle y la severidad, no solo el resumen

`PublicarComentario` mandaba al PR únicamente `hallazgo.Resumen`: el detalle con la
justificación y la sugerencia de arreglo, y la severidad, se descartaban. Se pagaba por
generarlos y no llegaban a nadie.

Un `FormateadorComentario` compone el cuerpo en Markdown y `Severidades` centraliza los
dos vocabularios (`error/warning/info` del modelo y `baja/media/alta` de la
configuración), que `FiltroRuido` duplicaba.

Etiquetas de texto y no emoji, para que rendericen igual en cualquier cliente. Sin firma
al pie: Bitbucket ya muestra el autor.

### A2 · No republicar comentarios ya publicados

El PR no se daba por revisado hasta el final del bucle de publicación, así que una caída
a mitad republicaba todo. Verificado con una sonda: caída en el tercero de cinco, la
vuelta siguiente publicaba los cinco.

`Hallazgo.Huella()` da identidad estable con archivo, línea y severidad. **Deja fuera la
prosa del modelo a propósito**: al reintentar se vuelve a llamar al LLM sobre el mismo
diff, y basta con que reformule una frase para que una huella basada en texto falle justo
en el caso que debe cubrir.

El ejecutor consulta antes de publicar y anota **después**: al revés, una caída entre
ambos perdería el hallazgo en silencio.

### A3 · Un comentario de resumen, con tope por pull request

Cada hallazgo era un comentario anclado independiente: doce hallazgos, doce
notificaciones para cada persona suscrita.

Solo los **Error** se anclan a su línea; el resto llega junto en un comentario de resumen
con el recuento por severidad. Un PR con 2 errores, 6 avisos y 4 notas pasa de **12
notificaciones a 3**. El resumen conserva el detalle de cada hallazgo.

Un PR limpio no recibe ningún comentario: el silencio significa «sin problemas».
Ajustable en `Llm.SeveridadAnclada`.

### A4 · Excluir rutas que no se revisan

Se enviaba el diff entero: ficheros de bloqueo, `node_modules`, `vendor`, `dist`, `bin`,
`obj`, minificados y generados entraban igual que el código escrito a mano.

`ExclusionRutas` empareja con globs y trae una lista de serie conservadora, configurable
en `Bitbucket:RutasExcluidas`. **Se aplica antes del tope de bytes** — un fichero de
bloqueo de 2 MB se comía el presupuesto y dejaba fuera el código que sí importa.

Las migraciones de base de datos **no** se excluyen: son generadas, pero esconderlas
taparía problemas reales.

### A5 · Comprobar la línea contra su archivo, no contra todo el diff

Los tramos de las cabeceras `@@` se acumulaban en una lista global. Un hallazgo en
`B.cs:11` pasaba si `A.cs` tenía un hunk que cubría la 11, así que en un PR de varios
ficheros la regla no descartaba casi nada.

Un mapa atribuye cada hunk a su archivo leyendo `diff --git`, `---` y `+++`. Un
renombrado acepta la ruta vieja y la nueva. Con dos archivos homónimos no adivina.

---

## P1 — Calidad de la revisión

Con P0 el revisor deja de molestar. Esto es lo que hace que además acierte.

### B1 · Numerar las líneas del diff

El prompt pedía al modelo inferir el número contando desde las cabeceras `@@`, que exige
llevar dos contadores en paralelo y avanzarlos distinto según la marca de cada línea.

`NumeradorDiff` antepone su número a cada línea: el del archivo nuevo para contexto y
añadidas, el del original para las eliminadas. Las cabeceras pasan intactas, con `---` y
`+++` comprobadas antes que la marca para no descuadrar los contadores.

### B2 · Dar contexto: intención del PR y entorno del cambio

Al modelo le llegaba el diff y nada más.

La intención viaja en un `ContextoRevision` — el título y la descripción **se perdían** al
convertir `EventoPr` en `PullRequest`. El contexto se pide con el parámetro `context` de
la propia API del diff, así que no cuesta ninguna llamada extra ni hay que descargar
ficheros enteros. Por defecto 10 líneas.

### B3 · Convenciones del equipo por repositorio

Un único prompt idéntico para todos los repositorios.

Un `.revisorpr.md` en la raíz del repositorio entra en el prompt como criterio de ese
equipo. Lo edita el propio equipo en un pull request suyo, sin tocar el servicio.

**Se lee de la rama de destino, nunca de la del PR**: si no, cualquiera podría incluir en
su pull request una guía que dijera «no reportes nada» y desactivar su propia revisión.

### B4 · Resistir instrucciones inyectadas desde el diff

Quien abre el PR controla el título, la descripción y el diff, y todo eso entra en el
prompt. Cuatro capas, de menos a más fiables:

1. Se le quitan al texto del autor las marcas de bloque, para que no pueda cerrarlas y
   escribir fuera.
2. **Un hallazgo que no hable de un archivo del PR se descarta.**
3. La severidad se reduce al juego conocido: se publica en negrita dentro de un
   comentario Markdown.
4. Topes de resumen, detalle y número de hallazgos.

Las que valen son la 2, la 3 y la 4: no dependen de que el modelo obedezca.

---

## P2 — Operación

### C1 · Integración continua

Las pruebas solo se ejecutaban si alguien se acordaba. `.github/workflows/ci.yml` compila
en Release con `-warnaserror` y ejecuta la suite en cada push y cada pull request contra
`main`.

Es la pieza que sostiene todas las demás: los defectos corregidos —incluido el que impedía
arrancar— habrían saltado en el primer push.

### C2 · Reaccionar por webhook, no solo por sondeo

Quien empujaba un commit esperaba hasta cinco minutos, y para entonces ya había cambiado
de tarea.

Un `ServidorWebhook` autentica el aviso y lo encola; el sondeo lo consume desde su mismo
hilo y bajo el mismo candado, porque el almacén es una sola conexión SQLite. El aviso y la
vuelta comparten las guardas, así que coincidir sobre un PR no lo revisa dos veces. **El
sondeo sigue como red de seguridad.**

Apagado de fábrica y **sin secreto no arranca**: un endpoint que dispara gasto en el
modelo no puede quedar sin autenticar.

> El nombre y formato de la cabecera de firma dependen de la versión de Bitbucket.
> Es configurable en `Webhook:CabeceraFirma`; **confírmalo contra tu instalación** antes
> de fiarte del camino HMAC. Si no la ofrece, el token en `Authorization: Bearer` funciona.

### C3 · Coste visible por pull request

No se registraban los tokens consumidos: nadie sabía qué se llevaba gastado.

`/estado` publica tokens acumulados, los de la última revisión y cuántas se han podido
medir. El reintento suma, y una revisión **fallida también cuenta**: se paga igual.

Se mide en tokens, no en dinero: los precios cambian y codificarlos aquí sería garantizar
que envejecen. Con `Llm:CostePorMillonEntrada` y `CostePorMillonSalida` el equipo pone su
tarifa; sin ellas la estimación sale nula, no cero.

### C4 · Que `/estado` muestre los fallos de listado

Los errores al listar un repositorio se registraban en el log pero no llegaban a
`EstadoServicio`: con credenciales caducadas, `/estado` devolvía `ultimosErrores: []`
mientras el servicio no revisaba nada. Es el modo de fallo más probable en producción y el
endpoint hecho para diagnosticarlo lo ocultaba.

### C5 · Limpiar el log

Un umbral fijo en `Information` ignoraba `Logging:LogLevel`, y el eco a `Console.Out`
duplicaba cada línea porque el host ya trae su propio proveedor de consola.

> **Cambio de opinión.** Se sustituyó el `File.AppendAllText` por línea por un fichero
> abierto, y se revirtió: en Windows, un fichero abierto para escritura no lo puede leer
> una herramienta que abra con `FileShare.Read` (Bloc de notas, `type`, `File.ReadAllText`).
> El operador no podría leer el log mientras el servicio corre, que es justo cuando lo
> necesita. La mejora era teórica; el coste, una regresión de uso real. Hay un test que
> lo fija para que nadie lo «optimice» otra vez.

---

## P3 — Deuda técnica

### D1 · Un PR nuevo esperaba una vuelta de más

Si en la vuelta había algún PR con commit nuevo, los completamente nuevos se descartaban
hasta la siguiente. No evitaba ningún problema —las guardas del almacén ya impiden revisar
dos veces— y hacía esperar un intervalo entero.

### D2 · Liberar las respuestas HTTP restantes

`ListarPrsAbiertos` y `PublicarComentario` no liberaban el `HttpResponseMessage`.
Resuelto de camino al hacer A2.

### D3 · Borrar el `appsettings.json` de la raíz

Estaba fuera del proyecto, no se cargaba nunca, y usaba `Llm:ApiKey` donde el código lee
`ClaveApi`. Una trampa para quien lo editara. Hay un test que impide que vuelva.

### D4 · Migraciones dentro de una transacción

El cambio de esquema y el registro de su versión eran operaciones sueltas. Resuelto de
camino al añadir la migración 5.

### D5 · Recuperación de JSON truncado sin coste cuadrático

Recorría el texto de derecha a izquierda probando seis sufijos fijos en cada punto de
corte, reparseando el prefijo entero en cada prueba.

Un solo recorrido con pila de llaves y corchetes, respetando cadenas y escapes. Además de
rápido es más correcto: el cierre sale de la pila, no de adivinar combinaciones. Con 4000
hallazgos y la respuesta cortada, se recuperan los 4000 en menos de tres segundos.

### D6 · Conservar el motivo del truncado en el reintento

En el primer intento se propagaba; en el del reintento se descartaba, ocultando
diagnóstico en el caso más raro.

### D7 · Credenciales fuera del fichero de configuración

Documentado `dotnet user-secrets` en desarrollo (el proyecto ya traía un `UserSecretsId`
sin usar) y variables de entorno en producción.

---

## Pendiente

Salió de revisar el resultado tras cerrar las cuatro bandas. Lo de arriba deja la
herramienta bien construida; esto es lo que falta para saber si además es **buena**.

### V1 · Piloto contra pull requests reales

**El servicio no ha revisado nunca un pull request de verdad.** Todas las llamadas al
modelo durante el desarrollo fueron contra dobles de prueba, y las ejecuciones reales
usaron `api.ejemplo.invalid` y credenciales falsas que devolvían 401.

Eso significa que la tasa de falsos positivos y la utilidad de los comentarios están
**sin medir**. Todo el valor del producto descansa en el prompt, y el prompt se ha
razonado pero nunca se ha visto funcionar.

Un repositorio, credenciales reales, un modelo real, `Llm:SeveridadAnclada` en `error` y
unos días de pull requests normales. Después, leer los comentarios y contar cuántos eran
útiles. Si de diez hallazgos hay siete razonables, hay producto; si hay tres, el problema
está en el prompt y ninguna cantidad de fontanería lo arregla.

**Es el ítem de más valor del backlog.** Dice más que los siguientes cinco juntos.

### V2 · Freno de gasto, no solo visibilidad

C3 hizo visible el coste, pero nada lo detiene. Con visibilidad y sin tope, la factura
sorpresa llega igual, solo que documentada.

Un presupuesto por periodo que, al agotarse, deje de llamar al modelo y lo diga en
`/estado` y en el log, en vez de seguir gastando en silencio.

### V3 · Marcar un hallazgo como incorrecto

El equipo no tiene forma de decir que un comentario estaba mal. Sin eso no hay manera de
medir la precisión ni de mejorarla salvo a ojo, y V1 se queda en una impresión en lugar
de en un número.

Lo más barato que funciona: una reacción o una palabra convenida en la respuesta al
comentario, que el servicio lea y contabilice.

### V4 · Confirmar la cabecera de firma del webhook

El nombre y el formato de la cabecera HMAC dependen de la versión de Bitbucket, y no se
ha podido verificar contra una instalación real. Es configurable en
`Webhook:CabeceraFirma` y el token en `Authorization: Bearer` funciona en cualquier caso,
pero mientras no se confirme, el camino HMAC no es de fiar.

---

## Cómo se verificó

Cada arreglo se comprobó **revirtiéndolo** para ver fallar sus tests. Los que describen
comportamiento en ejecución se verificaron levantando el servicio: el webhook responde
`202` a un aviso firmado y `401` sin credencial; `/estado` publica el bloque de coste y
los fallos de listado.

Tres tests afirmaban comportamientos que se cambiaron a propósito (D1, la severidad
desconocida y el hallazgo sin línea sobre un archivo ajeno). Se reescribieron explicando
el porqué en el propio test, no se ajustaron en silencio.

Nada de esto valida la **calidad de las revisiones**: las pruebas comprueban que la
tubería hace lo que debe con hallazgos de mentira. Para lo otro está V1.
