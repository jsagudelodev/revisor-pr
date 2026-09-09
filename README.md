Servicio de Windows que revisa automáticamente los pull requests de Bitbucket Cloud.

## Configuración

El servicio valida su configuración al arrancar y falla con un mensaje accionable si
falta algo. Copia `appsettings.example.json` sobre
`RevisorPrs.Servicio/appsettings.json` y rellena, como mínimo:

- `Bitbucket`: **uno** de los dos métodos de autenticación, nunca los dos a la vez.
  `MetodoAutenticacion: "Basica"` con `Usuario` + `ClaveAplicacion`, o
  `MetodoAutenticacion: "Token"` con `Token`.
- `Llm`: `Endpoint` y `Modelo` son obligatorios. `ClaveApi` no se exige al arrancar,
  pero sin ella la primera revisión fallará con un 401 del proveedor.
- `Sondeo`: `IntervaloMinutos` y al menos un repositorio en formato `espacio/repo`.

## Qué ve el equipo en el pull request

Los hallazgos graves se comentan **anclados a su línea**; el resto llega junto en un
único **comentario de resumen**. Así un pull request con doce hallazgos genera tres
notificaciones y no doce, que es lo que hace que un equipo acabe silenciando al revisor.

Cada comentario lleva la severidad, el resumen y el detalle con la sugerencia de arreglo.
Un hallazgo ya comentado no se repite aunque se empuje un commit nuevo, y un pull request
sin hallazgos no recibe ningún comentario: el silencio significa que está limpio.

El umbral se ajusta con `Llm.SeveridadAnclada` (por defecto `"error"`). Con `"baja"` se
ancla todo y no hay resumen.

## Qué ve el modelo

Se le da la **intención declarada** del pull request (título y descripción) para que
distinga un cambio deliberado que el autor explica de un descuido, y **10 líneas de
contexto** alrededor de cada cambio, para que no señale como error algo definido unas
líneas por encima del recorte. El contexto se pide a la propia API del diff, sin llamadas
extra; se ajusta con `Bitbucket.LineasDeContexto`.

## Ajustar el revisor a tu equipo

Crea un fichero `.revisorpr.md` en la raíz de tu repositorio con las convenciones del
equipo. El revisor las aplica como criterio propio: no señala como problema lo que la
guía permite y sí lo que la guía prohíbe. Lo que no mencione se juzga con el criterio
general.

```markdown
# Convenciones de revisión

- No usamos excepciones para flujo de control.
- Los tests siguen Given/When/Then y no comprueban más de un comportamiento.
- `async void` solo en manejadores de eventos.
- No hace falta comentar los DTO.
```

Lo edita el propio equipo en un pull request, como cualquier otro cambio: no hay que
tocar el servicio ni pedírselo a quien lo opera.

**La guía se lee de la rama de destino, nunca de la rama del pull request.** Es
deliberado: si se leyera del PR, cualquiera podría incluir en el suyo una guía que
dijera «no reportes nada» y desactivar su propia revisión. Cambiar la guía exige, por
tanto, pasar por una revisión del equipo.

La ruta se cambia con `Bitbucket.RutaGuiaRepositorio`; vacía desactiva la guía.

Cada línea del diff llega **ya numerada**, así que el modelo no tiene que deducir el
número contando desde las cabeceras `@@`.

Los ficheros que nadie revisa a mano —bloqueos de dependencias, `node_modules`, `vendor`,
`dist`, `bin`, `obj`, minificados y generados— no se envían al modelo. Se ajusta con
`Bitbucket.RutasExcluidas`.

Secciones opcionales: `Estado` (endpoint local de estado), `Registro` (log rotativo)
y `BaseDatos` (ruta del fichero SQLite; por defecto, junto al ejecutable).

Los valores se pueden dar también por variables de entorno con el separador `__`,
por ejemplo `Bitbucket__Usuario` o `Sondeo__Repositorios__0`.

### Dónde poner las credenciales

**No las dejes en `appsettings.json`.** Un fichero con credenciales junto al ejecutable
acaba copiado, comprimido o subido a algún sitio antes o después.

En desarrollo, el proyecto ya trae un `UserSecretsId`, así que basta con:

```
dotnet user-secrets --project RevisorPrs.Servicio set "Bitbucket:ClaveAplicacion" "..."
dotnet user-secrets --project RevisorPrs.Servicio set "Llm:ClaveApi" "..."
dotnet user-secrets --project RevisorPrs.Servicio set "Webhook:Secreto" "..."
```

Se guardan fuera del repositorio, en el perfil del usuario, y el servicio las lee sin
configurar nada más.

En producción, variables de entorno del propio servicio de Windows:

```
Bitbucket__ClaveAplicacion=...
Llm__ClaveApi=...
Webhook__Secreto=...
```

Pase lo que pase, el saneador impide que esos valores aparezcan en el log o en
`/estado`, incluida la credencial Basic ya codificada.

## Instalación como servicio de Windows

El proceso se registra en el Administrador de control de servicios con el nombre
`RevisorPrs`. Publica y da de alta el servicio con:

```
dotnet publish RevisorPrs.Servicio -c Release -o C:\RevisorPrs
sc.exe create RevisorPrs binPath= "C:\RevisorPrs\RevisorPrs.Servicio.exe" start= auto
sc.exe start RevisorPrs
```

Ejecutado directamente desde la consola funciona igual, sin necesidad de instalarlo.

## Revisión inmediata con webhook (opcional)

Con solo sondeo, quien empuja un commit espera hasta `Sondeo.IntervaloMinutos` a que le
llegue la revisión. Con el webhook activado, arranca en segundos.

Viene **apagado de fábrica** y exige un secreto: si se habilita sin `Webhook.Secreto`, el
servicio no arranca. Un endpoint que dispara revisiones —y por tanto gasto en el
modelo— no puede quedar sin autenticar.

```json
"Webhook": {
  "Habilitado": true,
  "Direccion": "127.0.0.1",
  "Puerto": 8788,
  "Ruta": "/webhook/bitbucket",
  "Secreto": "el-mismo-valor-que-en-bitbucket"
}
```

Se admiten dos formas de presentar el secreto, porque no todas las instalaciones de
Bitbucket ofrecen lo mismo: firma HMAC-SHA256 del cuerpo en la cabecera indicada por
`Webhook.CabeceraFirma`, o el secreto en `Authorization: Bearer`. **Confirma el nombre y
el formato de la cabecera de firma contra tu instalación de Bitbucket** antes de darlo
por bueno; si no la ofrece, usa el token.

`Direccion` es loopback por defecto: para que Bitbucket llegue hay que cambiarla a
propósito, normalmente detrás de un proxy inverso con TLS.

**El sondeo sigue activo como red de seguridad**, para los avisos que se pierdan, los
pull requests abiertos antes de configurar el webhook y los momentos en que la cola se
llene. Un aviso y una vuelta que coincidan sobre el mismo PR no lo revisan dos veces.

## Endpoint de estado

Con `Estado.Habilitado` (por defecto, activo) el servicio publica `GET /estado` con la
última vuelta, la próxima, los contadores de pull requests y los últimos errores.

Solo escucha en loopback: una `Estado.Direccion` que no sea `127.0.0.1`, `::1` o
`localhost` tira el servicio al arrancar en lugar de exponer el estado en una interfaz
pública. La respuesta pasa por el saneador de secretos antes de salir.

## Cuánto cuesta

El endpoint `/estado` publica los tokens consumidos: acumulados, los de la última
revisión y cuántas revisiones se han podido medir. Cada revisión deja también una línea
en el log con su coste.

Se cuenta en **tokens** y no en dinero porque es lo que devuelve el proveedor y no
caduca. Si quieres ver una estimación en dinero, pon tus tarifas:

```json
"Llm": {
  "CostePorMillonEntrada": 3,
  "CostePorMillonSalida": 15
}
```

Con las tarifas a cero —por defecto— solo se informan tokens y `costeEstimadoAcumulado`
sale nulo: el servicio no inventa precios que envejecerían mal.

El coste de una revisión que **falló** también se cuenta: se paga igual, y ocultarlo
falsearía la cifra justo cuando más interesa mirarla. Y si el proveedor no informa del
uso, la revisión funciona igual: simplemente no hay cifra.

## Registro

El log se escribe a fichero junto al ejecutable (configurable en `Registro.RutaFichero`)
y rota por tamaño. Nunca se registra el contenido de un diff ni una credencial: los
mensajes que contienen marcas de diff se sustituyen enteros, y las claves configuradas
se enmascaran dondequiera que aparezcan, incluida la credencial Basic ya codificada.

## Qué pasa si alguien intenta manipular la revisión

Quien abre un pull request controla el título, la descripción y el diff, y todo eso entra
en el prompt. La defensa es en capas, y las que valen son las que **no dependen de que el
modelo obedezca**:

- Un hallazgo que no hable de un archivo del pull request se descarta.
- La severidad se reduce al juego conocido; el modelo no escribe texto libre en la
  cabecera del comentario.
- El resumen y el detalle se recortan, y hay un tope de hallazgos por revisión.
- El material del autor va delimitado y se le quitan las marcas de bloque, para que no
  pueda cerrarlo y escribir fuera.

La guía del equipo se lee de la rama de destino, así que tampoco puede alterarse desde el
propio pull request.

## Aviso sobre el proveedor de LLM

El diff de cada pull request se envía al proveedor de LLM configurado en la sección
`Llm` de `appsettings.json`. Esto significa que **el contenido del código revisado
viaja fuera de la infraestructura de Bitbucket**.

Por seguridad, conviene elegir un proveedor:

- **sin retención de prompts** (que no guarde los mensajes para reentrenamiento ni
  para analítica), o
- **local / on-premise** (un modelo autoalojado al que no llegue código de terceros).

Consulta la política de uso de datos del proveedor antes de activarlo en un
repositorio con código sensible.
