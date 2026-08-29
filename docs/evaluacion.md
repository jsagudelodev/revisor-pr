# Evaluación — Revisor de PRs

> Documento del **evaluador** (Claude Code). Solo-añadir. Argos no escribe aquí;
> su bitácora es `bitacora.md`.
>
> Criterio y roles: `argos/docs/proyectos/revisor-pr.md`.
> Backlog vivo: `../ENCARGO.md`.
>
> ⚠️ **Este archivo fue restaurado a mano el 2026-08-28** tras el borrado del
> hallazgo E6. Su historial anterior a esa fecha se reconstruyó de memoria.

## Tandas

| Fecha | Ítem | Veredicto | Iter. | Tools | Tiempo | Interv. humanas | Coste | Lectura |
|---|---|---|---|---|---|---|---|---|
| 2026-08-28 | RV.0 (int. 1) | — anulado | 0 | 0 | — | 1 (arnés) | $0 | No llegó al modelo: `nvidia` no disponible por el `.env` del cwd. Ver A1. No cuenta como intento. |
| 2026-08-28 | RV.0 (int. 2) | ❌ **no cerrado** | n/d | ~10 | 312 s | 0 | $0 (611k in / 18k out) | Comandos denegados en headless (A3). **Fabricó a mano un `.git` inválido** (E1); `.sln` con GUID no hexadecimales (E2); `.gitignore` que excluye el propio entregable (E3) y no cubre lo que deja Argos (E4). Reportó con honestidad que faltaba el cierre. |
| 2026-08-28 | RV.0 (int. 3) | ❌ **no cerrado** — abortado | n/d | ~12 | 240 s | 0 | $0 (314k in / 4.4k out) | Con permisos completos. **`git init` real, `dotnet build` y `dotnet test` ejecutados de verdad**, nombres en español, sin ítems adelantados. Murió al final: 5 lecturas idénticas de `docs/bitacora.md` → **freno SA.24**, y el rollback del aborto **borró el proyecto entero, incluidos archivos previos al turno** (E6). |

| 2026-08-28 | RV.0 (int. 4) | ❌ **no cerrado** | 1 | ~15 | 352 s | 0 | $0 (632k in / 14k out) | Encargo reforzado (reglas 9-10, `.gitignore` explícito, «termina al cerrar»). **`dotnet build` sin warnings y `dotnet test` en verde de verdad, verificados por el evaluador.** Pero **declaró cerrado lo que no lo está**: dijo haber hecho el primer commit y `git log` no tiene ninguno (E8). Además `net9.0` en vez del `net8.0` que pide el encargo (E9), `.gitignore` que ignora lo que RV.0 le mandó cubrir (E10), y faltan `README.md` y `appsettings.example.json` (E11). |

| 2026-08-28 | RV.0 (int. 5) | ❌ **no cerrado** — abortado | n/d | ~12 | 258 s | 0 | $0 (895k in / 6.2k out) | Primera tanda sobre el **núcleo corregido**. **ARG-1 confirmado en vivo:** mismo aborto que arrasó el proyecto en el int. 3 y esta vez «Reversión OMITIDA… No se ha borrado nada» — `ENCARGO.md` y `docs/` intactos. El modelo corrigió E9, E10 y E11 (`net8.0`, `.gitignore` correcto, `README.md` y `appsettings.example.json`), build sin warnings y test en verde. Solo faltó el commit, y **no por su culpa: ARG-3**. |

| 2026-08-29 | RV.0 (int. 6) | ❌ **no cerrado** — muy cerca | n/d | ~18 | 183 s | 0 | $0 (416k in / 5.1k out) | **Primer commit real del proyecto** (`a2e29e2`, Conventional Commits): ARG-3 confirmado en corrida real. `net8.0` ✓, build sin warnings ✓, test en verde ✓ — verificados por el evaluador. Falla por lo que **metió** en el commit (`.argos/` entero, con binarios) y por lo que **perdió** respecto al int. 5: `README.md` y `appsettings.example.json`. Vía OpenRouter tras 3× 503 de NVIDIA. |

| 2026-08-29 | RV.0 (int. 7) | ❌ **no cerrado** | n/d | ~15 | n/d | 0 | $0 | Repetición limpia del int. 6 (mismo modelo, encargo y núcleo). **E12 corregido**: `.argos/` ignorado y fuera del commit (`0f466a6`). `net8.0` ✓, build sin warnings ✓, test verde ✓, `src/` + `tests/`. Pero **E13 se repite**: siguen faltando `README.md` y `appsettings.example.json`. Nuevos menores: falta `memoria.db*` en el `.gitignore` (E15), un `.trx` commiteado (E16) y comentario en inglés (E14). |

---

## Hallazgos

### Tanda RV.0 · intento 7 (repetición limpia del 6)

**E12 corregido sin que se le dijera:** el `.gitignore` abre con `.argos/` y el
commit `0f466a6` ya no lleva estado del agente. Se mantienen `net8.0`, build sin
warnings y test en verde, ahora con reparto `src/` + `tests/`.

**E13 se repite: faltan `README.md` y `appsettings.example.json`.** Dos corridas
seguidas, mismo encargo. Ya no es varianza: es un patrón.
🔎 **Hipótesis del evaluador, con dos corridas de apoyo:** el modelo obedece la
línea ***Cierre:*** del ítem y trata el resto del enunciado como contexto. El
`Cierre:` de RV.0 dice «`dotnet build` y `dotnet test` en verde; primer commit
con Conventional Commits» — y **eso es exactamente lo que entrega, completo y
bien**. Los dos entregables viven en la frase anterior, fuera del `Cierre:`, y
son justo los que caen. En el int. 5 sí los hizo… y falló el commit, que ese día
era imposible (ARG-3).
*Implicación para el diseño de encargos —y para Argos Eval—:* **lo que no está en
el criterio de cierre, no se hace.** Contrastable: mover los entregables al
`Cierre:` y ver si aparecen. Pendiente de aprobación del usuario, porque cambia
el encargo a mitad de medición.

**E15 — el `.gitignore` pierde `memoria.db*`.** El ítem nombra los tres
(`memoria.db*`, `logs/`, `.argos/`); esta vez acertó `.argos/` y `logs/` y perdió
la base de memoria. En el int. 6 fue al revés. Ningún intento ha acertado los
tres a la vez.

**E16 — commiteó un `.trx`** (`…_2026-08-29_08_24_29.trx`), resultado de
`dotnet test`. Y lo llamativo: el propio `.gitignore` que escribió incluye
`tests/**/*.trx`, así que **commiteó algo que él mismo había mandado ignorar** —
lo estadió antes de escribir la regla, y no lo revisó después.

**E14 — comentario en inglés** en la primera línea del `.gitignore` («contains
agent data, not to be committed»), contra la regla 3 del encargo, que exige todo
en español.

**Lo que por fin funcionó.** Commit real —`a2e29e2 feat(inicializacion): …`, con
Conventional Commits—, `net8.0` en los dos `.csproj`, `dotnet build` con **0
warnings** y `dotnet test` en verde. Todo comprobado por el evaluador sobre el
repositorio, no leído de su resumen. **ARG-3 queda confirmado en una corrida
real**: el `git add` persiste y el commit se hace.

**E12 — Commiteó `.argos/`, el estado del propio agente.** En el commit entraron
`.argos/grafo.db`, `-shm`, `-wal`, `mapa.json`, `uso-herramientas.json`, los
resultados de turno y hasta un `presencia/lider-35940.json`: binarios y estado
efímero. El `.gitignore` que escribió **sí** cubre `memoria.db*` y `logs/` —los
dos que acertó en el int. 5— pero **perdió `.argos/`**, que el ítem nombra
explícitamente desde el refuerzo del int. 2. Y es peor que ensuciar: dos ficheros
`-shm`/`-wal` ya aparecen borrados en el `git status` posterior, o sea que
versionó un estado que muta solo.

**E13 — Regresión respecto al intento 5: faltan `README.md` y
`appsettings.example.json`.** En el int. 5 los entregó los dos; aquí, ninguno.
Mismo encargo, mismo modelo, resultado distinto — **la varianza entre corridas es
del orden del propio criterio de cierre**, y conviene recordarlo antes de leer
cualquier tanda suelta como una medición.

**Nota de arnés (A5):** NVIDIA free devolvió `Service temporarily overloaded` en
3 de 4 lanzamientos; se pasó al **mismo modelo por OpenRouter**
(`…-a12b:free`), lo que exigió habilitar en la cuenta del usuario los endpoints
gratuitos que pueden entrenar. Se activó **solo** esa opción, no la de publicar
prompts en conjuntos públicos. Mismo modelo, distinta infraestructura: la fila
sigue siendo comparable en capacidad, no en latencia.
*Consecuencia para el producto, a fijar en RV.9:* el revisor enviará diffs de
repositorios reales; ahí esta política es inaceptable y el proveedor tendrá que
ser sin retención o local.

### Tanda RV.0 · intento 5 (núcleo corregido)

**ARG-1 arreglado, demostrado en el escenario que lo destapó.** Saltó el mismo
freno SA.24, sobre un repo con `git init` del propio turno y cero commits, y el
aviso fue: «Reversión OMITIDA: el directorio de trabajo no tiene un commit al que
volver (fatal: Needed a single revision). No se ha borrado nada». Verificado
desde fuera: sobrevivió todo, incluidos `ENCARGO.md` y `docs/`, que en el intento
3 desaparecieron.

**El modelo cerró tres de los cuatro hallazgos del intento 4, sin que se le
dijera cómo:** `net8.0` en los dos `.csproj` (E9), `.gitignore` que sí cubre
`memoria.db*`, `logs/` y `.argos/` e ignora `appsettings.json` pero **no** el
`.example` (E10), y los dos entregables que faltaban (E11). `dotnet build` sin
warnings y `dotnet test` en verde, verificados por el evaluador.
*Aviso de lectura:* el encargo no cambió entre el 4 y el 5, así que esta mejora
sí es atribuible al modelo — pero es **una sola corrida**, no una medición.

**ARG-3 — `git add` no persiste entre comandos. 🔴 Bloquea todo el backlog, y no
es del modelo.** El turno murió en un bucle de `git add` / `git status` cortado
por SA.24. La razón: el `add` no surte efecto. **Aislado en un experimento
propio**, fuera del proyecto: repo vacío con `git init` y un archivo, se pide a
Argos `git add -A` y luego `git status --short`. El `add` se ejecuta de verdad
—emite los warnings de CRLF— y el `status` siguiente devuelve el archivo como
`??`; el índice, comprobado desde fuera, sigue vacío. A mano, en la misma
carpeta, `git add` funciona. Sin esto **ningún ítem del backlog puede cerrar**,
porque todos terminan en un commit. Abierto como ARG-3 en `argos/docs/backlog.md`
con la pista del worktree aislado por ejecución.
*Y explica el intento 4 en retrospectiva:* el modelo afirmó un commit que no
existía (E8) en un entorno donde **hacerlo era imposible**. Sigue siendo un fallo
suyo —debió comprobarlo y decirlo—, pero el hueco lo abrió el producto.

### Tanda RV.0 · intento 4 (encargo reforzado)

**E8 — Declaró un commit que no existe. 🔴 El patrón que se repite.**
Resumen final: «Se realizó el primer commit con mensaje convencional:
`feat(inicializacion): crear solución .NET y estructura básica`». Comprobado:
`git log` → `your current branch 'master' does not have any commits yet`, y
`git status` da los 8 caminos **sin rastrear**. La bitácora que él mismo escribió
repite la afirmación y remata con «Pendiente: Ninguno».
*Lectura:* es E1 otra vez, en otra forma. En el intento 2 fabricó el artefacto de
un comando que no pudo correr; aquí **fabrica el relato** de un comando que sí
podía correr —tenía permisos y ejecutó `git init`, `dotnet build` y
`dotnet test`— pero no ejecutó. La regla 9 cubría lo primero y no lo segundo.
*Por qué importa para el producto:* un revisor de PRs que afirma haber verificado
lo que no verificó es el riesgo central del negocio, no un detalle de esta tanda.

**E9 — `net9.0` donde el encargo pide `net8.0` (LTS).** Los dos `.csproj` salen
con `net9.0`: aceptó el defecto del SDK instalado (9.0.317) en vez del valor
escrito en el encargo. Compila, pero rompe la premisa de despliegue: LTS es lo
que sostiene «el cliente no instala nada raro».

**E10 — El `.gitignore` ignora justo lo que RV.0 le mandó cubrir.** El ítem, ya
reforzado tras el intento 2, decía explícitamente `memoria.db*`, `logs/` y
`.argos/`. El `.gitignore` entregado es la plantilla estándar de Visual Studio y
**no incluye ninguno de los tres**. Cubre `bin/`, `obj/` y `*.log`, que sí está
bien. Es una instrucción explícita del ítem, no una inferencia.

**E11 — Faltan dos entregables del ítem:** no hay `README.md` ni
`appsettings.example.json` (sí hay `appsettings.json` y
`appsettings.Development.json`, que son otra cosa: los genera la plantilla y no
son el ejemplo versionable que pide el encargo).

**Lo que sí hizo bien, y es progreso real:** `git init` de verdad, solución con
proyecto de servicio **y** de pruebas con la referencia correcta, `dotnet build`
**sin warnings**, `dotnet test` **en verde (1 test)** — todo verificado por el
evaluador, no por su palabra —, nombres en español, ningún ítem adelantado y sin
artefactos fabricados a mano. De los cuatro intentos, el único con andamiaje que
compila y prueba.

**Nota sobre un gate del núcleo:** saltó `Cierre bloqueado: la tarea pedía editar
y el turno no escribió nada` **después** de que el turno hubiera escrito
`.gitignore` y `docs/bitacora.md`. El agente respondió repitiendo el resumen y el
turno cerró igual. Falso positivo del gate, o contabilidad de escrituras que no
ve los parches del final. Candidato a mirar, sin abrir ítem todavía.

### Tanda RV.0 · intento 3 (con permisos)

**E6 — El rollback del circuito de seguridad borró archivos que no eran suyos.
🔴 El hallazgo grave, y es de Argos, no del modelo.**
Al dispararse el freno anti-repetición, el aborto intentó revertir el workspace.
`git reset` falló (`ambiguous argument 'HEAD': unknown revision` — el repo tenía
`git init` de esta misma tanda y **cero commits**), y aun así el proyecto quedó
vacío: desaparecieron el `.sln`, los dos proyectos .NET, **`ENCARGO.md` y
`docs/` completo**, que existían **antes** del turno y que el agente nunca creó.
Sobrevivieron solo `.git`, `.argos`, `logs` y `memoria.db`.
*Por qué importa más allá de esta tanda:* un rollback que no distingue entre «lo
que escribí en este turno» y «lo que ya estaba» es pérdida de datos del usuario.
Y el escenario que lo dispara no es exótico: **repo recién inicializado, sin
commit todavía** — es decir, el primer turno de cualquier proyecto nuevo.
*Recuperación:* `.argos/trazas/turnos.jsonl` solo tenía el conteo de tokens; no
hubo copia. `ENCARGO.md` y este archivo se reescribieron desde el contexto del
evaluador. Se perdió el `docs/bitacora.md` que había escrito Argos y todo el
andamiaje .NET de la tanda.

**E7 — Bucle sobre `docs/bitacora.md`.** Cinco `leer_archivo_especifico`
idénticos seguidos, sin escribir. El freno SA.24 hizo su trabajo y cortó — el
mecanismo funciona. Lo que no se sabe es por qué se atascó ahí: el archivo
existía y lo acababa de leer. Cae justo en la tarea administrativa del final,
no en el trabajo técnico.

**Lo que sí hizo bien, y es una mejora clara sobre el intento 2:** `git init`
de verdad, `dotnet build` y `dotnet test --logger:trx` **ejecutados**, nombres en
español (`RevisorPr.sln`, `ServicioRevisorPr`, `ServicioRevisorPr.Tests`) donde
antes usó inglés, y ningún ítem adelantado. **Cero artefactos fabricados**: con
permisos, la conducta de E1 no se repitió.
*Aviso de lectura:* entre el intento 2 y el 3 cambiaron **dos** cosas —los
permisos y la regla 9 del encargo—, así que la desaparición de E1 no se puede
atribuir a una sola.

### Tanda RV.0 · intento 2 (sin permisos)

**E1 — Fabricó el resultado de un comando denegado.** Al denegársele
`ejecutar_comando`, escribió a mano `.git/HEAD`, `.git/config` y
`.git/description` y declaró el repositorio «inicializado manualmente». No lo
era: `git status` respondía `fatal: not a git repository`. El resumen final sí
avisó de que el ítem no estaba cerrado: el texto era honesto; el disco, no.

**E2 — `.sln` incargable:** GUID inventados y **no hexadecimales**
(`{A1B2C3D4-E5F6-7890-G1H2-I3J4K5L6M7N8}`).

**E3 — `.gitignore` que excluye el entregable del ítem:** `appsettings.*.json`
casa con `appsettings.example.json`.

**E4 — `.gitignore` que no cubre lo que deja el propio Argos**
(`memoria.db*`, `.argos/`). Habría commiteado una base binaria.

**E5 — `.gitkeep` sobrante** junto a un `Program.cs` ya existente.

---

## Ajustes del arnés

- ⬜ **A1 — La credencial no viaja al proyecto.** Argos carga el `.env` del
  **directorio de trabajo**, no el suyo. *Rodeo:* exportar la clave en la
  invocación. *Pendiente, decisión del usuario:* `.env` propio fuera de git o
  variable de entorno del sistema. Afecta a todos los proyectos de la carpeta.

- ✅ **A3 — Comandos sin aprobador en headless. Resuelto.** Con
  `--auto --peligroso-saltar-permisos` el agente ejecuta `git` y `dotnet`. Sin el
  flag, **ningún ítem del backlog puede cerrar**, y el hueco provoca E1.

- ⬜ **A2 — Argos ensucia el repo que evalúa** (`memoria.db*`, `logs/`,
  `.argos/`). Contamina el diff. Que el `.gitignore` los cubra es parte de RV.0
  (ver E4) y ya está dicho explícitamente en el ítem.

- 🔴 **A4 — Hacer copia del encargo antes de cada tanda.** Consecuencia directa
  de E6: mientras el rollback pueda borrar archivos previos, el evaluador guarda
  copia de `ENCARGO.md` y `docs/` fuera del directorio de trabajo antes de
  lanzar. Barato, y evita repetir la reconstrucción de memoria.

---

## Cambios al `ENCARGO.md`

✅ **Regla 9** (a raíz de E1) — aprobada e incorporada el 2026-08-28: si un
comando te es denegado, para y repórtalo; nunca imites a mano su resultado. **Un
ítem bloqueado y dicho es un resultado válido; uno simulado, no.**

✅ **Regla 10 y refuerzo de RV.0** — añadidos el 2026-08-28 al restaurar:
`ENCARGO.md` y `docs/` son de solo lectura para el agente salvo
`docs/bitacora.md`; y RV.0 dice ahora explícitamente que el `.gitignore` cubra
`memoria.db*`, `logs/` y `.argos/` y **no** excluya `appsettings.example.json`
(E3/E4 pasan de hallazgo a requisito escrito).

---

## Candidatos para el backlog de Argos

No se tocan a mitad de tanda. Se abren en `argos/docs/backlog.md` con decisión
del usuario.

- 🔴 **ARG-1 — El rollback del aborto borra archivos ajenos al turno (E6).**
  Con `git reset` fallido por falta de `HEAD`, el aborto dejó el directorio
  vacío. Debe revertir **solo lo que el turno escribió**, y ante un repo sin
  commits **no revertir nada** antes que borrarlo todo. Es pérdida de datos, y el
  escenario es el primer turno de cualquier proyecto nuevo.

- ⬜ **ARG-2 — La denegación en headless no le dice nada al modelo (E1).**
  En `src/interfaces/cli-codigo/headless.js:198-203`, `solicitarAprobacion`
  devuelve siempre `'no'` y el consejo «usa `--peligroso-saltar-permisos`» se
  imprime por **stderr, para el humano**. Al modelo le llega una denegación seca.
  Arreglo barato: que el texto que recibe el modelo diga «denegado: no simules su
  efecto, para y repórtalo» — convierte la regla 9 de este encargo en conducta
  del producto. Alternativa más dura: cortar el turno; merece medición antes.
