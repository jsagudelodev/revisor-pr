# Encargo — Revisor de PRs (servicio de Windows, .NET)

> **Para Argos.** Este documento y `docs/` son lo único que existe en el repo al
> empezar. Todo lo demás — incluido el `git init`, la solución `.sln` y el primer
> test — lo construyes tú. El humano **no escribe código**: aprueba, rechaza y
> anota.

---

## Qué se construye

Un **servicio de Windows** que revisa automáticamente los pull requests de
**Bitbucket Cloud** y publica sus hallazgos como comentarios en el propio PR.

Funciona **por sondeo, no por webhook**: cada N minutos pregunta a Bitbucket qué
PRs abiertos tienen commits sin revisar. Esto es deliberado y es el argumento de
venta — el servicio corre dentro de la red del cliente y **no necesita abrir
ningún puerto entrante**; solo hace llamadas HTTPS salientes.

Ciclo de cada vuelta:

1. listar los PRs abiertos de los repositorios configurados,
2. quedarse con los que tienen un commit que aún no se revisó,
3. descargar el diff de cada uno,
4. pasarlo a un revisor (LLM) que devuelve hallazgos estructurados,
5. publicar los hallazgos como comentarios,
6. registrar qué `(repositorio, PR, commit)` quedó revisado.

**V1 NO hace** (no se negocia dentro de V1): webhooks, GitHub, UI web,
multi-tenant, facturación, aprobar o bloquear merges, revisar código fuera de un
PR. Lo único que escucha en red es `/estado`, **atado a `127.0.0.1`**.

---

## Stack

- **.NET 8 (LTS), C#.** Worker Service
  (`Microsoft.Extensions.Hosting.WindowsServices`), no una web app.
- **`HttpClient` vía `IHttpClientFactory`** para Bitbucket y para el LLM. Sin SDK
  de Bitbucket, sin SDK de LLM: son llamadas HTTP y JSON.
- **SQLite con `Microsoft.Data.Sqlite` directo. Sin EF Core, sin ORM.**
- **xUnit** para los tests.
- Se publica **self-contained**: el cliente no instala ningún runtime.
- Nada de Docker, ni colas externas, ni servicios en la nube.

Si en algún ítem crees que hace falta un paquete que no está en esta lista,
**pregunta antes de añadirlo**; no lo metas por tu cuenta.

---

## Reglas de trabajo

1. **Un ítem `RV.*` por tanda.** No adelantes el siguiente aunque «ya esté casi».
2. **Cada ítem cierra con `dotnet build` sin warnings y `dotnet test` en verde**,
   más lo que diga su línea *Cierre:*. Un ítem sin su cierre demostrado no está
   cerrado.
3. **Todo en español:** nombres de archivo, clases, métodos, variables,
   comentarios y commits. Convenciones de C# (PascalCase en clases y métodos,
   camelCase en locales), pero con palabras en español.
4. **Commits en Conventional Commits** (`feat(sondeo): …`).
5. **Sin `Console.WriteLine` de depuración, sin código comentado, sin TODO** que
   no apunte a un ítem de este backlog.
6. **Los tests prueban comportamiento, no la existencia del archivo.** Un test que
   pasa borrando la aserción cuenta como ítem no cerrado.
7. **Errores:** nunca se publica un stack trace ni un mensaje técnico en un PR.
   Una vuelta de sondeo que falla **no puede tumbar el servicio**: se registra y
   se sigue en la siguiente.
8. **Secretos:** la app password de Bitbucket, la clave del LLM y el contenido del
   diff **nunca** se escriben en el log.
9. **Si una herramienta o un comando te es denegado, para y repórtalo.** Nunca
   imites a mano el resultado que ese comando habría producido: no crees un
   `.git` escribiendo sus archivos, no redactes de memoria un `.sln` o cualquier
   otro archivo que genera una herramienta, no des por pasado un test que no
   corriste. **Un ítem bloqueado y dicho es un resultado válido; uno simulado,
   no.** Si el bloqueo impide cerrar el ítem, déjalo abierto y explica qué
   comando te faltó.
10. **`ENCARGO.md` y `docs/` son de solo lectura para ti**, salvo `docs/bitacora.md`,
    donde añades una fila por tanda (solo-añadir): fecha, ítem, qué hiciste,
    iteraciones, tests añadidos, qué quedó pendiente. **Nunca borres ni
    reescribas `ENCARGO.md` ni `docs/evaluacion.md`.**

---

## Backlog

Estado: ⬜ pendiente · 🔄 en curso · ✅ cerrado · ⏸ diferido.
Los ⏸ **no se tocan** en V1.

### 🟦 E1 — Esqueleto y sondeo

- ⬜ **RV.0 — Inicializar el repo.**
  `git init`, solución .NET con el proyecto del servicio y el de tests,
  `.gitignore`, `appsettings.example.json`, `README.md` de una línea.
  El `.gitignore` debe cubrir también lo que deja el propio agente
  (`memoria.db*`, `logs/`, `.argos/`) y **no** debe excluir
  `appsettings.example.json`.
  *Cierre:* (1) existen en la raíz `README.md` y `appsettings.example.json`;
  (2) el `.gitignore` contiene `memoria.db*`, `logs/` y `.argos/`, y **no**
  excluye `appsettings.example.json`; (3) `dotnet build` sin warnings y
  `dotnet test` en verde sobre un clon limpio; (4) primer commit con Conventional
  Commits, y **nada de `.argos/`, `logs/`, `bin/`, `obj/` ni `.trx` dentro de
  ese commit**.

- ⬜ **RV.1 — El servicio y su bucle.**
  Worker Service que despierta cada N minutos (intervalo configurable) y ejecuta
  una vuelta de sondeo. Configuración con los repositorios a vigilar; el arranque
  **falla fuerte y con mensaje accionable** si falta un valor obligatorio.
  *Cierre:* test que corre una vuelta con un reloj falso, sin esperar de verdad.

- ⬜ **RV.2 — Qué hay que revisar.**
  De la lista de PRs abiertos, quedarse solo con los que tienen un commit que aún
  no se revisó. En la primera vuelta sobre un repositorio nuevo **no revisa el
  histórico entero**: solo lo abierto de ahora en adelante.
  *Cierre:* (1) existe una pieza propia que, dada la lista de PRs abiertos y lo ya
  revisado, decide cuáles hay que revisar; (2) tests de **los tres casos**: PR
  nuevo → se revisa; PR ya revisado sin cambios → NO se revisa; PR con commit
  nuevo sobre una revisión previa → se revisa; (3) un test de la primera vuelta
  sobre un repositorio nuevo que demuestre que **no** se devuelve el histórico
  entero; (4) los tests corren sin red y sin esperas reales; (5) `dotnet build`
  sin warnings, `dotnet test` en verde y commit con Conventional Commits, sin
  `bin`, `obj`, `.argos`, `logs` ni `.trx`.

- ⬜ **RV.3 — Evento normalizado.**
  De la respuesta cruda de Bitbucket a un tipo propio
  `EventoPr { Repositorio, Numero, Commit, Titulo, Rama }`. Lo que no se entienda
  se descarta con log, no con excepción.
  *Cierre:* una respuesta real guardada como fixture y un test que la traduce.

### 🟩 E2 — Cliente de Bitbucket Cloud

- ⬜ **RV.4 — Autenticación y listado.**
  App password (usuario + token) por configuración; listar los PRs abiertos de un
  repositorio, **con paginación** — Bitbucket pagina y un repo activo tiene más
  PRs de los que caben en una página.
  *Cierre:* test con dos páginas mockeadas que verifica que se recorren ambas.

- ⬜ **RV.5 — Leer el diff** de un PR.
  *Cierre:* test con la red mockeada **más** una corrida manual contra un PR real.

- ⬜ **RV.6 — Publicar comentario.**
  Comentario anclado a línea cuando Bitbucket lo permita; si no, comentario
  general con `archivo:linea` en el texto.
  *Cierre:* test que verifica el cuerpo enviado, no solo que se llamó.

- ⬜ **RV.7 — Reintentos y límite de tasa.**
  Backoff ante 429 y 5xx, tope de intentos, y error accionable al agotarlos (qué
  repositorio, qué PR, qué código HTTP). Bitbucket Cloud **limita por hora**: el
  sondeo no puede quemar la cuota en una sola vuelta.
  *Cierre:* test que simula 429 → 429 → 200 y comprueba que no duplica la llamada.

- ⬜ **RV.8 — Diffs grandes.**
  Recortar por archivo con un tope de bytes configurable y decir en el comentario
  qué archivos se omitieron. **Nunca** mandar un diff de 2 MB al modelo.
  *Cierre:* test con un diff por encima del tope.

### 🟨 E3 — El revisor

- ⬜ **RV.9 — Revisar el diff.**
  Del diff a una lista de
  `Hallazgo { Archivo, Linea, Severidad, Resumen, Detalle }`.
  Prompt propio, en español.

- ⬜ **RV.10 — Respuesta mal formada.**
  Si el modelo devuelve texto en vez de JSON, se reintenta **una** vez pidiendo
  solo JSON; si vuelve a fallar, ese PR queda marcado como fallido con su motivo.
  **Nunca** se publica basura en el PR.
  *Cierre:* tests con respuesta válida, con JSON envuelto en markdown y con prosa.

- ⬜ **RV.11 — Filtro de ruido.**
  Descartar los hallazgos por debajo de una severidad configurable y los que
  apunten a líneas que no están en el diff.
  *Cierre:* test con un hallazgo fuera de rango y otro por debajo del umbral.

- ⬜ **RV.12 — Cambiar de modelo sin tocar código.**
  Proveedor de LLM y modelo por configuración, para revisar el mismo PR con dos
  modelos y comparar.
  *Cierre:* test que cambia de proveedor por configuración.

### 🟧 E4 — Persistencia e idempotencia

- ⬜ **RV.13 — SQLite.**
  Qué se ha revisado y qué se ha comentado. Migraciones versionadas en código,
  aplicadas al arrancar. La base vive junto al servicio, no en `%TEMP%`.
  *Cierre:* test que arranca sobre una base vacía y sobre una ya migrada.

- ⬜ **RV.14 — Idempotencia.**
  Volver a procesar el mismo `(repositorio, PR, commit)` no publica ni un
  comentario nuevo. Esto importa más aquí que con webhooks: el sondeo **vuelve a
  ver los mismos PRs cada vuelta**.
  *Cierre:* test que corre dos vueltas seguidas y cuenta las llamadas a publicar.

- ⬜ **RV.15 — Aislar por repositorio.**
  El servicio debe poder vigilar **varios repositorios a la vez** sin mezclar sus
  datos: lo que se guardó para uno no puede aparecer al consultar otro.
  *Cierre:* migración aplicada sobre una base **con datos previos, sin
  perderlos**, y un test que **falla** si dos repositorios comparten resultados.

### 🟪 E5 — Resistencia

- ⬜ **RV.16 — Uno a la vez.**
  Los PRs de una vuelta se procesan en serie; una vuelta nueva no arranca si la
  anterior sigue corriendo.
  *Cierre:* test que dispara dos vueltas solapadas y comprueba que no se pisan.

- ⬜ **RV.17 — Sobrevive al reinicio.**
  Lo pendiente vive en SQLite, no en memoria: al reiniciar el servicio retoma sin
  volver a comentar lo ya comentado.
  *Cierre:* test que interrumpe a mitad y arranca de nuevo.

- ⬜ **RV.18 — Un PR que falla no para el resto.**
  Se marca fallido con su motivo y la vuelta continúa con el siguiente. Un fallo
  repetido en el mismo PR no lo reintenta indefinidamente cada vuelta.
  *Cierre:* test con un PR que revienta y otro detrás que sí termina.

### ⬛ E6 — Instalable en casa del cliente

- ⬜ **RV.19 — Publicar e instalar.**
  `dotnet publish` self-contained a un único directorio, y las instrucciones para
  registrarlo como servicio de Windows con arranque automático y reinicio ante
  fallo. **El cliente no instala ningún runtime.**
  *Cierre:* instalado y arrancando en una máquina limpia siguiendo solo el README.

- ⬜ **RV.20 — `/estado` local.**
  Endpoint mínimo **atado a `127.0.0.1`**: última vuelta, PRs revisados, próximo
  sondeo, proveedor y modelo activos, últimos errores.
  *Cierre:* test que verifica que no escucha en una interfaz pública.

- ⬜ **RV.21 — Log a fichero**, con rotación, sin volcar el diff ni los secretos.
  *Cierre:* test que asegura que un token presente en la configuración no aparece
  en ninguna línea de log.

- ⬜ **RV.22 — README real.**
  De cero a un PR comentado, con `appsettings.example.json` completo y el paso a
  paso de la app password de Bitbucket.

### 🔶 Diferido — NO se toca en V1

- ⏸ **RV.23** Webhook como alternativa al sondeo (para quien tenga el servicio
  expuesto o use Bitbucket Data Center).
- ⏸ **RV.24** Soporte de GitHub.
- ⏸ **RV.25** Modo «revisar en frío»: pasar N PRs ya mergeados por línea de
  comandos, para fabricar el informe de venta.
- ⏸ **RV.26** Aprender del feedback de los comentarios resueltos.
- ⏸ **RV.27** Multi-tenant y despliegue como servicio gestionado.

---

## Lo que necesita el humano antes de RV.4

App password de Bitbucket Cloud con permiso de **leer pull requests y escribir
comentarios**, en una **cuenta propia del bot**, no la personal — los comentarios
deben verse firmados por el producto. Hasta que exista, RV.4 y RV.5 se cierran
con la red mockeada y la corrida real queda anotada como pendiente en la bitácora.
