# 06 — Sincronización por vault con GitHub

Este es el subsistema más crítico de Basalt. Su requisito supremo es: **nunca perder datos y converger siempre**. Toda decisión de diseño de este documento se subordina a ello.

## 1. Modelo

- **Un repositorio de GitHub por vault, una rama por vault.** El repositorio contiene exactamente el árbol del vault (la raíz del repo = la raíz del vault).
- **Sincronización diferenciada por vault**: cada vault tiene su propio `SyncProfile` (cuenta, repo, rama, disparadores, políticas, exclusiones, mensaje de commit), su propio estado (`SyncBase`, caché de hashes, journal, log) y su propio ciclo de vida. Los vaults sincronizan en paralelo y de forma aislada: un error, bloqueo o conflicto en un vault no afecta a otro.
- **Sin Git local**. No se crea carpeta `.git` ni se usa libgit2/CLI de git. Se usa la **API REST de GitHub (Git Data API)** directamente sobre HTTPS. Motivos (ver ADR-0002): funciona igual en iOS (sin binarios nativos ni procesos), sin dependencias nativas, control total de errores y pruebas deterministas con un servidor falso.
- **El repositorio es un repositorio Git normal**: el usuario puede clonarlo, editar en github.com o con otras herramientas. Basalt DEBE tolerar commits ajenos (de otros clientes, de la web, incluso force-push).
- **Modelo de fusión**: tres vías por fichero, con la **base de sincronización** (manifiesto del último estado acordado *por fichero*) como ancestro común. No se depende del grafo de commits.

## 2. Perfil de sincronización (`SyncProfile`)

Almacenado **en el dispositivo** (`ISyncProfileStore`, `{AppData}/vaults/{vaultId}/sync/profile.json`), no en el repo, porque contiene decisiones locales (cuenta, frecuencia).

```csharp
public sealed record SyncProfile(
    VaultId VaultId,
    GitHubAccountId AccountId,
    RemoteRepositoryRef Repository,        // host, owner, name, branch
    bool Enabled,
    bool Paused,
    SyncTriggers Triggers,
    SyncPolicy Policy,
    SyncScope Scope,
    CommitSettings Commit);

public sealed record SyncTriggers(
    bool OnVaultOpen = true,
    TimeSpan? Interval = null,              // por defecto 5 min; null = desactivado; mínimo 1 min
    TimeSpan? AfterEditIdle = null,         // por defecto 30 s tras el último guardado; null = desactivado
    bool OnAppBackground = true,            // al pasar a segundo plano / cerrar
    bool BackgroundRefresh = true,          // iOS BGTaskScheduler (best-effort)
    bool OnlyOnUnmeteredNetwork = false);   // iOS: solo Wi-Fi

public sealed record SyncPolicy(
    TextConflictPolicy TextConflicts = TextConflictPolicy.AutoMergeOrConflictCopy,
    BinaryConflictPolicy BinaryConflicts = BinaryConflictPolicy.KeepBoth,
    DeleteConflictPolicy DeleteVsModify = DeleteConflictPolicy.KeepModified,
    long MaxFileSizeBytes = 50 * 1024 * 1024);   // tope absoluto 100 MB (límite de GitHub)

public enum TextConflictPolicy { AutoMergeOrConflictCopy, AutoMergeOrMarkers, PreferLocal, PreferRemote }
public enum BinaryConflictPolicy { KeepBoth, PreferLocal, PreferRemote }
public enum DeleteConflictPolicy { KeepModified, PreferDelete }

public sealed record SyncScope(
    bool IncludeAttachments = true,
    bool IncludeObsidianConfig = false,     // carpeta .obsidian/
    bool IncludeTrash = false,              // carpeta .trash/
    IReadOnlyList<string> ExtraIgnorePatterns);

public sealed record CommitSettings(
    string MessageTemplate = "Sync from {device}: {summary}",
    string? AuthorNameOverride = null,
    string? AuthorEmailOverride = null);
```

## 3. Qué se sincroniza

Se sincronizan **todos los ficheros del vault** salvo los ignorados. Reglas de exclusión (`IgnoreRules`), en este orden:

1. **Siempre ignorados (no configurables)**: `.git/`, `.basalt/workspace.*.json`, `.basalt/cache/`, `.~basalt-*.tmp`, `.DS_Store`, `Thumbs.db`, `desktop.ini`, `Icon\r`, `*.swp`, ficheros > `MaxFileSizeBytes`, enlaces simbólicos.
2. Por `SyncScope`: `.trash/` (si `!IncludeTrash`), `.obsidian/` (si `!IncludeObsidianConfig`), adjuntos (todo lo no-`.md` fuera de `.basalt/`, si `!IncludeAttachments`).
3. `.basalt/syncignore` del vault (sintaxis gitignore, **se sincroniza**, compartido por todos los dispositivos).
4. `ExtraIgnorePatterns` del perfil (solo este dispositivo).
5. **Cuarentena**: rutas remotas que no se pueden materializar en este dispositivo (§7.4, §7.5).

Ficheros que **sí** se sincronizan dentro de `.basalt/`: `vault.json`, `types.json`, `bookmarks.json`, `syncignore`, `templates.json` y cualquier otro ajuste compartido.

> Si el vault contiene una carpeta `.git/` (el usuario usa git por su cuenta), Basalt la ignora y muestra un aviso persistente en los ajustes de sincronización: "Este vault también es un repositorio git local; usar ambos mecanismos a la vez puede producir commits duplicados".

**Ficheros ignorados que ya existen en remoto**: si un patrón de ignorado cubre una ruta que existe en remoto, Basalt NO la borra en remoto ni la descarga; simplemente queda fuera del plan (ni local ni remoto la tocan).

## 4. Estado local de sincronización (por vault)

En `{AppData}/vaults/{vaultId}/sync/`:

| Fichero | Contenido |
|---------|-----------|
| `profile.json` | `SyncProfile` |
| `base.json` | `SyncBase`: `LastSyncedCommit`, `LastRemoteTree`, `Manifest` (ruta → sha, tamaño) — **por fichero** |
| `hashcache.json` | `(ruta, tamaño, mtimeUtcTicks) → BlobSha` para evitar rehash |
| `remote-trees/{treeSha}.json` | Caché de manifiestos remotos por árbol (LRU, máx. 3) |
| `journal.json` | Journal del ciclo en curso (diagnóstico y limpieza) |
| `staging/{sha}` | Blobs descargados/preparados del ciclo en curso |
| `overwritten/{timestamp}/...` | Versiones locales sobrescritas por políticas `Prefer*` (retención 30 días) |
| `log.jsonl` | Registro de ciclos (últimos 500) |
| `quarantine.json` | Rutas remotas no materializables y motivo |

Todos los ficheros JSON se escriben de forma atómica. La pérdida o corrupción de `hashcache.json`, `remote-trees/` o `journal.json` NO afecta a la corrección (solo al rendimiento). La pérdida de `base.json` degrada a "fusión inicial" (§10, modo Merge): no se pierden datos, pero pueden aparecer copias de conflicto.

## 5. Planificación: tabla de decisión (`SyncPlanner`, dominio puro)

Para cada ruta `p` en `B ∪ L ∪ R` (base, local, remoto; `∅` = ausente, letras = blob SHA):

| # | B | L | R | Condición | Acción |
|---|---|---|---|-----------|--------|
| 1 | b | b | b | — | Nada |
| 2 | b | x | b | x≠b | **Upload** x |
| 3 | b | b | y | y≠b | **Download** y |
| 4 | b | x | x | x≠b | Nada (mismo cambio en ambos lados) — actualizar base |
| 5 | b | x | y | x≠y, x≠b, y≠b, texto | **MergeText**(b, x, y) según `TextConflicts` |
| 6 | b | x | y | ídem, binario | Conflicto binario según `BinaryConflicts` |
| 7 | b | ∅ | b | — | **DeleteRemote** |
| 8 | b | b | ∅ | — | **DeleteLocal** (esperado b) |
| 9 | b | ∅ | ∅ | — | Nada — quitar de la base |
| 10 | b | x | ∅ | x≠b | Modificado local / borrado remoto → `KeepModified`: **Upload** x · `PreferDelete`: **DeleteLocal** con copia en `overwritten/` |
| 11 | b | ∅ | y | y≠b | Borrado local / modificado remoto → `KeepModified`: **Download** y · `PreferDelete`: **DeleteRemote** (la versión y sigue en el historial de git) |
| 12 | ∅ | x | ∅ | — | **Upload** x |
| 13 | ∅ | ∅ | y | — | **Download** y |
| 14 | ∅ | x | x | — | Nada — añadir a la base |
| 15 | ∅ | x | y | x≠y | Añadido en ambos lados con contenido distinto → conflicto (texto: `MergeText` con base vacía, que solo es limpia si un lado está vacío; binario: según política) |

**Texto vs binario**: un fichero es texto si su extensión está en la lista de texto (`md, txt, json, yaml, yml, css, js, canvas, csv, svg, html, xml`) **y** su contenido es UTF-8 válido sin bytes NUL en los primeros 8 KB. Si alguno de los tres lados no es texto → binario.

**Resolución de conflictos de texto (fila 5 y 15)**:

| Política | Fusión limpia | Fusión con conflictos |
|----------|---------------|-----------------------|
| `AutoMergeOrConflictCopy` (defecto) | Se aplica el resultado en ambos lados | En la ruta queda la **versión remota**; la versión local se guarda como **copia de conflicto** `Nombre (conflict YYYY-MM-DD HHmmss Dispositivo).md` que también se sube. Se registra el conflicto. |
| `AutoMergeOrMarkers` | Ídem | En la ruta queda el texto con **marcadores** `<<<<<<< Dispositivo` / `=======` / `>>>>>>> GitHub`; se sube; se registra el conflicto |
| `PreferLocal` | Sin fusión: gana local; la remota se guarda en `overwritten/` | — |
| `PreferRemote` | Sin fusión: gana remota; la local se guarda en `overwritten/` | — |

**Conflictos binarios** (`KeepBoth`): en la ruta queda la versión remota y la local se sube como copia de conflicto. `Prefer*`: gana un lado, el otro a `overwritten/`.

Elegir "remoto gana en la ruta, local va a copia" garantiza **convergencia**: todos los dispositivos acaban con el mismo contenido en la ruta original con independencia del orden en que sincronizan.

**Invariantes del planificador** (property tests obligatorios, ver [09](09-estrategia-de-pruebas.md) §6):
- P1. *Sin pérdida*: para todo `p`, cada SHA en `{L[p], R[p]}` distinto de `B[p]` acaba en el manifiesto objetivo (en `p`, en una copia de conflicto) o, solo con políticas `Prefer*`/`PreferDelete`, en `overwritten/` o en el historial de git.
- P2. *Idempotencia*: `Plan(T, T, T) = ∅` donde `T` es el manifiesto objetivo de un plan; y aplicar un plan y volver a planificar con los manifiestos resultantes da un plan vacío.
- P3. *Simetría de convergencia*: dos dispositivos que sincronizan en cualquier orden contra el mismo remoto, sin más ediciones, terminan con manifiestos idénticos tras ≤ 2 ciclos cada uno.
- P4. *Monotonía de la base*: la base nueva solo contiene SHAs presentes en L, R o resultados de fusión.
- P5. Ninguna acción `DeleteLocal` sin `expectedSha`.

## 6. Ciclo de sincronización (algoritmo)

Componentes en `Basalt.Application/Sync` (cada uno con una responsabilidad, inyectados por interfaz):

| Componente | Responsabilidad |
|------------|-----------------|
| `SyncCoordinator` (singleton) | Disparadores, cola por vault, coalescencia, backoff, estado observable de todos los vaults |
| `VaultSyncEngine` (scope vault) | Ejecuta un ciclo completo (pasos 1–12) |
| `LocalSnapshotScanner` | Enumera, filtra con `IgnoreRules`, hashea con caché → `Manifest` local + incidencias |
| `RemoteSnapshotProvider` | Head remoto (con ETag) + manifiesto del árbol (con caché por tree SHA) |
| `SyncPlanner` (dominio) | Plan puro |
| `PlanMaterializer` | Descarga a staging, verifica SHA, ejecuta fusiones (dominio), nombra copias de conflicto → *manifiesto objetivo* `T` + contenidos |
| `RemotePublisher` | Sube blobs, crea árbol, commit y actualiza rama (fast-forward) |
| `LocalApplier` | Aplica escrituras/borrados locales con concurrencia optimista |
| `SyncStateCommitter` | Calcula y guarda la nueva base, caché de hashes, log; limpia journal y staging |

### Pasos

```
0. Precondiciones: perfil habilitado y no pausado; online; token disponible. Si no → estado correspondiente, fin.
   Adquirir el SyncLock del vault (no bloquea la edición). Publicar FlushPendingEditsRequested y esperar ≤ 2 s.
1. ESCANEO LOCAL → L
   - Enumerar ficheros; aplicar IgnoreRules; excluir cuarentena.
   - Para cada fichero: si (ruta, tamaño, mtime) está en hashcache → sha; si no, hashear en streaming.
   - Detectar colisiones de mayúsculas locales (solo FS sensible) → incidencia, se excluyen las rutas en colisión salvo la primera ordinal.
2. HEAD REMOTO
   - GET ref con If-None-Match (ETag guardado). 404 de rama → R = ∅ (rama/repo vacío).
3. MANIFIESTO REMOTO → R
   - Si head.tree está en caché → usarla; si no, GET tree recursivo (§9.3) y cachear.
   - Rutas remotas no materializables (§7.4/§7.5) → cuarentena, excluidas.
4. PLAN = SyncPlanner.Plan(B, L, R, policy)
   - Si vacío y head == B.LastSyncedCommit → actualizar "último sync", fin (estado UpToDate).
   - Si vacío pero head distinto → guardar base con nuevo commit, fin.
5. MATERIALIZAR
   - Descargar blobs necesarios (Download, y para MergeText: base y remoto) a staging, verificando GitBlobHasher(bytes) == sha.
     · Si se necesitan > 200 blobs, usar zipball del commit (§9.5) y verificar cada SHA; los que no cuadren, por API de blobs.
     · Base no disponible en remoto (404 por historial reescrito) → tratar como fila 15 (sin base).
   - Ejecutar fusiones y resolver conflictos según política → T (manifiesto objetivo) + contenidos nuevos en staging.
6. JOURNAL: guardar {intento, head esperado, T, operaciones locales y remotas}.
7. PUBLICAR (si T ≠ R)
   - Blobs: los textos UTF-8 se envían inline en create-tree (campo "content"); los binarios con POST blobs (base64). Omitir blobs cuyo SHA ya existe en R o B.
   - create-tree con base_tree = R.tree y solo los cambios (upsert / delete con sha:null), troceado en peticiones ≤ 8 MB encadenadas.
   - Verificar que el TreeSha devuelto coincide con el calculado localmente para T (si difiere: error interno "sync.tree_mismatch", abortar sin aplicar).
   - create-commit (parents = [head] o [] si repo vacío; autor §8).
   - PATCH ref con force=false. Si 422/409 "not a fast forward" o el head cambió → volver al paso 2 (máx. 5 intentos, backoff 1s·2^n + jitter). El staging se reutiliza.
   - Repo totalmente vacío (409 "Git Repository is empty"): inicializar con PUT contents de ".basalt/vault.json" y volver al paso 2.
8. APLICAR LOCAL
   - Para cada ruta con T[p] ≠ L[p]: tomar el bloqueo de escritura del vault solo para esa operación;
     escribir/borrar con expectedSha = L[p] (para creaciones, "no debe existir").
   - Si la verificación falla (el usuario editó durante la sync) → ruta "aplazada", no se toca.
   - Borrar carpetas que queden vacías por borrados de la sync.
   - Publicar FilesChangedBySync(rutas) → índice, editores abiertos (F-003.2/F-003.3), explorador.
9. NUEVA BASE
   - Para cada ruta: si aplazada → B'[p] = B[p] (base antigua); si no → B'[p] = T[p].
   - LastSyncedCommit = nuevo head (o head remoto si no se publicó).
   - Guardar base (atómico), hashcache (con mtime/tamaño de los ficheros escritos), limpiar journal y staging.
10. LOG y EVENTOS: añadir entrada a log.jsonl, publicar SyncCompleted(outcome). Si hubo aplazadas → reprogramar sync en 5 s.
```

**Tolerancia a fallos**: la corrección NO depende del journal. Si el proceso muere en cualquier punto, el siguiente ciclo vuelve a planificar con la base antigua y la tabla de decisión converge:
- Muerte antes del paso 7 → no ha cambiado nada fuera de staging.
- Muerte tras publicar y antes de aplicar → los ficheros subidos coinciden con R (fila 4), los pendientes de descarga se descargan (fila 3), las fusiones se re-fusionan contra su propio resultado (limpio).
- Muerte durante la aplicación local → idem; las escrituras son atómicas por fichero.
Al arrancar, si existe `journal.json`, se registra "ciclo interrumpido" en el log, se limpia staging huérfano y se programa una sync inmediata.

## 7. Casos especiales

### 7.1 Edición concurrente durante la sincronización
Cubierto por la concurrencia optimista del paso 8 y la base por fichero del paso 9. El editor nunca pierde pulsaciones: si el fichero cambia en disco con el buffer sucio, aplica F-003.3.

### 7.2 Renombrados
Se modelan como borrado + alta (Git no tiene renombrados). Optimización en `LocalApplier`: si un `Download` de sha `s` coincide con un fichero local que se va a borrar con el mismo sha, se hace `Move` local en lugar de descargar. Un renombrado local + edición remota del mismo fichero produce: alta nueva (local), y en la ruta vieja fila 11 (borrado local / modificado remoto) → con `KeepModified` reaparece la ruta vieja con la edición remota. Esto es correcto (sin pérdida) y se documenta en la ayuda.

### 7.3 Conflicto fichero/carpeta
Si en un lado `a` es fichero y en el otro `a/` es carpeta: el **fichero** se renombra a `a (conflict … Dispositivo)` en ambos lados y la carpeta se mantiene.

### 7.4 Colisiones de mayúsculas
Si R contiene rutas que difieren solo en mayúsculas (`Nota.md`, `nota.md`) y el FS local es insensible: se materializa la primera en orden ordinal; las demás van a **cuarentena** con motivo `case_collision`. Como `Manifest` exige portabilidad y rechaza dos rutas con la misma `CaseFoldKey` (02 §11), la cuarentena se aplica en todos los SO, no solo en los sensibles a mayúsculas: es la opción conservadora y la ruta remota nunca se borra. La UI de sincronización lista las rutas en cuarentena con la sugerencia de renombrarlas en GitHub. Las rutas en cuarentena nunca generan `DeleteRemote`.

### 7.5 Rutas no válidas en esta plataforma
Rutas remotas que violan `VaultPath` en este SO (p. ej. `aux.md`, `a:b.md`, segmentos terminados en punto en Windows) → cuarentena con motivo `invalid_path`. Igual tratamiento.

### 7.6 Ficheros grandes
- Locales > `MaxFileSizeBytes` → no se suben; aviso "N ficheros superan el límite".
- Remotos > `MaxFileSizeBytes` → se descargan igualmente si ≤ 100 MB (no excluir datos ajenos); > 100 MB no pueden existir (límite GitHub).

### 7.7 Historial reescrito (force-push ajeno)
No se exige ascendencia entre `LastSyncedCommit` y head. La base por fichero sigue siendo válida; si un blob de base ya no existe en el servidor, se trata como "sin base" (fila 15).

### 7.8 Repo pertenece a otro vault
Si al configurar, el remoto tiene `.basalt/vault.json` con otro `VaultId`, se pregunta: "Adoptar la identidad del repositorio" (el VaultId local se sustituye; la migración de índice/estado es por id) o "Cancelar".

### 7.9 Fin de línea y codificación
Se sincronizan **bytes exactos**. No hay conversión CRLF/LF (equivale a `core.autocrlf=false`). El editor preserva el fin de línea de cada fichero.

### 7.10 Permisos perdidos / repo eliminado / renombrado
- 401 → `AuthRequired` (cuenta), todas las vaults de esa cuenta se pausan automáticamente.
- 403 sin rate limit → `remote.forbidden` (sin permiso de escritura).
- 404 de repo → `remote.not_found`.
- 301 (repo renombrado/transferido) → seguir la redirección y actualizar `owner/name` en el perfil, con notificación.
- Ninguno de estos errores provoca borrados locales.

## 8. Commits

- **Mensaje**: plantilla con variables `{device}`, `{summary}` (`2 modificados, 1 añadido, 1 eliminado`), `{date}`, `{vault}`. Cuerpo: hasta 20 rutas cambiadas (`M path`, `A path`, `D path`, `C path` para conflicto) + `… y N más`. Trailers:
  ```
  Basalt-Device: MacBook de Boris
  Basalt-Device-Id: 3f2c…
  Basalt-Version: 1.0.0
  ```
- **Autor y committer**: nombre del usuario de GitHub (o login) y email `{id}+{login}@users.noreply.github.com`; sobrescribibles en el perfil. Fecha del `IClock`.
- Un ciclo produce **como máximo un commit**.

## 9. Integración con la API de GitHub

### 9.1 Autenticación

- **OAuth Device Flow** (RFC 8628) con una **OAuth App** de Basalt; `client_id` inyectado en build (`BASALT_GITHUB_CLIENT_ID`, en `appsettings.json` embebido; no es secreto). Scopes: `repo` (repos privados) y `read:user`.
  1. `POST https://github.com/login/device/code` → `device_code`, `user_code`, `verification_uri`, `interval`, `expires_in`.
  2. Mostrar `user_code`, copiar al portapapeles, abrir `verification_uri` (`IExternalLauncher`).
  3. Sondear `POST https://github.com/login/oauth/access_token` cada `interval` s; manejar `authorization_pending`, `slow_down` (+5 s), `expired_token`, `access_denied`.
  4. `GET /user` → login, id, avatar, nombre.
  5. Guardar cuenta (`IAccountStore`) y token (`ISecretStore`).
- Si el token incluye `refresh_token`/`expires_in` (GitHub App con caducidad), `IGitHubTokenProvider` refresca automáticamente 5 min antes de caducar.
- **Alternativa**: pegar un *fine-grained personal access token* (necesario para GitHub Enterprise Server sin OAuth App). Se valida con `GET /user` y se comprueba acceso al repo al configurar.
- El token **nunca** se escribe en logs, mensajes de error, ficheros de estado ni URLs (handler de logging HTTP con redacción de `Authorization`; test que lo verifica).

### 9.2 Cliente HTTP

- `HttpClient` tipado `GitHubApiClient` por host, creado con `IHttpClientFactory`. Cabeceras: `Accept: application/vnd.github+json`, `X-GitHub-Api-Version: 2022-11-28`, `User-Agent: Basalt/{versión}`, `Authorization: Bearer {token}` (añadida por un `DelegatingHandler` que consulta `IGitHubTokenProvider`).
- **Resiliencia** (`Microsoft.Extensions.Http.Resilience`): reintentos solo para métodos idempotentes y POST de blobs/árboles/commits (son idempotentes por contenido), ante 5xx, timeouts y errores de red: 3 reintentos, backoff exponencial con jitter. **PATCH de ref no se reintenta automáticamente** (lo gestiona el bucle del paso 7). Timeout por intento 30 s (100 s para blobs > 10 MB y zipball).
- **Límites de tasa**: leer `x-ratelimit-remaining`/`x-ratelimit-reset`; si `remaining < 50` → pausar syncs automáticas de esa cuenta hasta `reset`. 403/429 con `retry-after` (límite secundario) → esperar ese tiempo. Concurrencia máxima por cuenta: 4 peticiones simultáneas; peticiones que crean contenido: ≤ 60/min por cuenta (token bucket).
- **Peticiones condicionales**: GET de ref con `If-None-Match`; un 304 no consume cuota → permite intervalos de 1 min.
- JSON con `System.Text.Json` source-generated (`GitHubJsonContext`).
- Mapeo de errores HTTP → códigos de `Error`: `remote.unauthorized`, `remote.forbidden`, `remote.not_found`, `remote.rate_limited` (con `RetryAt`), `remote.non_fast_forward`, `remote.conflict`, `remote.validation_failed`, `remote.server_error`, `remote.network`, `remote.timeout`, `remote.repository_empty`.

### 9.3 Endpoints utilizados

| Operación | Endpoint |
|-----------|----------|
| Usuario | `GET /user` |
| Listar repos | `GET /user/repos?per_page=100&sort=updated&affiliation=owner,collaborator,organization_member` (paginación por `Link`) |
| Crear repo | `POST /user/repos` o `POST /orgs/{org}/repos` (`auto_init: false`) |
| Info repo | `GET /repos/{owner}/{repo}` (permisos, rama por defecto, archivado, visibilidad) |
| Head de rama | `GET /repos/{o}/{r}/git/ref/heads/{branch}` |
| Commit | `GET /repos/{o}/{r}/git/commits/{sha}` (para obtener tree) |
| Árbol | `GET /repos/{o}/{r}/git/trees/{tree}?recursive=1`; si `truncated: true` → recorrer subárboles no recursivamente, en paralelo (máx. 4) |
| Blob | `GET /repos/{o}/{r}/git/blobs/{sha}` con `Accept: application/vnd.github.raw+json` (bytes directos) |
| Crear blob | `POST /repos/{o}/{r}/git/blobs` (`encoding: base64`) |
| Crear árbol | `POST /repos/{o}/{r}/git/trees` (`base_tree`, entradas con `sha`, `content` o `sha: null` para borrar) |
| Crear commit | `POST /repos/{o}/{r}/git/commits` |
| Crear rama | `POST /repos/{o}/{r}/git/refs` |
| Actualizar rama | `PATCH /repos/{o}/{r}/git/refs/heads/{branch}` (`force: false`) |
| Repo vacío | `PUT /repos/{o}/{r}/contents/.basalt/vault.json` |
| Zipball | `GET /repos/{o}/{r}/zipball/{commitSha}` (sigue redirección a codeload) |
| Historial de fichero | `GET /repos/{o}/{r}/commits?path={p}&sha={branch}&per_page=50` |
| Fichero en commit | `GET /repos/{o}/{r}/contents/{p}?ref={sha}` (raw) |

### 9.4 Cálculo local de árboles

Para verificar el paso 7 y evitar trabajo innecesario, `Domain.Sync` incluye `GitTreeHasher`: calcula el `TreeSha` de un manifiesto siguiendo el formato de objetos tree de Git (entradas `"{mode} {name}\0{20 bytes sha}"` ordenadas con la regla de Git: los directorios se comparan como si su nombre terminase en `/`). Probado contra SHAs conocidos de repos reales (vectores de prueba en el repo de tests).

### 9.5 Zipball

Para descargas masivas (clonado inicial, > 200 blobs): descargar el zip del commit concreto a staging, recorrer las entradas (quitando el prefijo de carpeta raíz `owner-repo-sha/`), calcular el SHA de cada fichero y aceptar solo los que coincidan con el manifiesto. Los ausentes o distintos (p. ej. por `export-ignore` en `.gitattributes`) se descargan por la API de blobs.

## 10. Vinculación inicial (`ConfigureSync`)

El caso de uso realiza primero un **dry-run** (pasos 1–5 sin publicar ni aplicar) y muestra un resumen antes de confirmar:

| Local | Remoto | Modo | Comportamiento |
|-------|--------|------|----------------|
| Vacío o solo `.basalt/` | Vacío | Init | Commit inicial con `.basalt/vault.json` |
| Con contenido | Vacío | Upload | Commit inicial con todo el vault |
| Vacío | Con contenido | Download | Descarga todo (equivale a clonar) |
| Con contenido | Con contenido | Merge | Base vacía: ficheros iguales se adoptan; distintos → conflictos según política (fila 15). La UI muestra "N iguales, M solo local, K solo remoto, C en conflicto" y pide confirmación |

Otras validaciones: el repo existe y es accesible; `permissions.push == true`; no archivado; rama existente (o crearla desde la rama por defecto); aviso si el repo es **público**.

`CloneVaultFromRepository` = crear vault local vacío con el `VaultId` de `.basalt/vault.json` remoto (o uno nuevo si no existe) + perfil + modo Download.

## 11. Disparadores y planificación (`SyncCoordinator`)

- Fuentes: `Manual`, `VaultOpened`, `Interval`, `AfterEditIdle` (cada `NoteSaved` reinicia el temporizador), `AppBackgrounding`, `AppClosing` (escritorio: espera máx. 10 s con UI "Sincronizando antes de salir…" y botón "Salir ahora"), `BackgroundRefresh` (iOS), `ConnectivityRestored`, `Retry`.
- **Cola por vault**: como máximo un ciclo en ejecución y uno pendiente por vault (coalescencia: los disparadores durante un ciclo se fusionan en un único "re-ejecutar al terminar").
- **Paralelismo entre vaults**: hasta 3 vaults sincronizando a la vez (configurable), respetando los límites por cuenta (§9.2).
- **Backoff ante errores transitorios**: 30 s, 1 min, 2 min, 4 min… máx. 30 min, con jitter ±20 %; se reinicia con un éxito o un disparador manual.
- **Errores no transitorios** (`AuthRequired`, `remote.forbidden`, `remote.not_found`): se detienen los disparadores automáticos del vault hasta intervención del usuario.
- iOS: `BGAppRefreshTask` con identificador `net.basalt.sync.refresh`; al ejecutarse sincroniza los vaults con `BackgroundRefresh` en serie dentro del tiempo concedido; si se agota, cancela de forma segura (los pasos son cancelables y la corrección no depende de completar).

## 12. Estado observable

```
                ┌──────────────┐
                │ NotConfigured│
                └──────┬───────┘ ConfigureSync
                       ▼
 ┌────────┐  pause  ┌──────┐  trigger  ┌──────────┐ success  ┌──────────┐
 │ Paused │◀───────▶│ Idle │──────────▶│ Syncing  │─────────▶│ UpToDate │
 └────────┘ resume  └──┬───┘           │(fase, %) │          └────┬─────┘
                       │               └─┬──┬──┬──┘               │ edición local
                       │     offline     │  │  │ error transitorio ▼
                       │◀────────────────┘  │  └──────────▶ ┌──────────────┐
                       │                    │               │ Error(retry) │
                       │      401           ▼               └──────────────┘
                       │              ┌──────────────┐
                       └─────────────▶│ AuthRequired │
                                      └──────────────┘
Superpuestos: PendingChanges(n), Conflicts(n), Quarantined(n), Offline
```

`SyncStatus` (DTO) = `{ State, Phase?, Progress?, PendingLocalChanges, LastSuccessAt, LastError?, NextRetryAt?, ConflictCount, QuarantineCount }`. El número de cambios pendientes se calcula de forma barata comparando eventos de guardado con la base (sin rehash completo).

Fases mostradas: `Escaneando`, `Comprobando remoto`, `Descargando (n/m)`, `Fusionando`, `Subiendo (n/m)`, `Aplicando`, `Finalizando`.

## 13. Centro de conflictos e historial

- **Conflictos registrados** en `{AppData}/vaults/{id}/sync/conflicts.json` (local) con: ruta, tipo (texto/binario/borrado), copia de conflicto creada, SHAs (base, local, remoto), fecha.
- Además, cualquier nota con marcadores `<<<<<<<`/`>>>>>>>` válidos o con nombre `* (conflict * *)` aparece como conflicto (detección en el índice), de modo que los conflictos generados en **otro** dispositivo también se ven aquí.
- UI de resolución: diff en tres columnas (local / resultado / remoto), aceptar bloque izquierdo/derecho/ambos, edición manual del resultado. Al confirmar: escribe el resultado en la ruta, elimina la copia de conflicto, marca resuelto y dispara sync.
- **Historial de versiones** (F-015.7) a partir de la API de commits; restaurar = escribir el contenido antiguo como una edición local normal.

## 14. Servidor GitHub falso (`Basalt.Testing.FakeGitHub`)

Requisito para las pruebas de integración. Implementado con ASP.NET Core minimal APIs, ejecutable en Kestrel sobre `127.0.0.1:{puerto libre}` (HTTP real) y también en `TestServer`.

DEBE implementar todos los endpoints de §9.3 y §9.1 (device flow con `/login/device/code` y `/login/oauth/access_token`) con:
- **Almacén de objetos Git real**: blobs, trees, commits con SHA calculados exactamente como Git (reutiliza `GitBlobHasher`/`GitTreeHasher` y añade `GitCommitHasher`), de modo que los SHAs coinciden con los de GitHub para el mismo contenido.
- Semántica de refs con fast-forward check (un commit es FF si el head actual es ancestro del nuevo).
- Repos vacíos (409 en Git Data API hasta el primer PUT contents), permisos (`push` true/false), archivado, visibilidad, redirección 301 por renombrado.
- ETag/304 en refs, cabeceras de rate limit con cuota configurable, `truncated: true` cuando el árbol supera un umbral configurable, zipball real, paginación con `Link`.
- **Inyección de fallos programable** (`FaultPlan`): fallar la N-ésima petición a un endpoint con un código, latencia, cortes de conexión, respuesta truncada, `retry-after`, y **hooks de carrera** (`BeforeUpdateRef(callback)`) para simular que otro dispositivo publica justo antes.
- Device flow configurable: aprobar tras N sondeos, `slow_down`, denegación, expiración.
- Registro de peticiones para aserciones (`server.Requests`), comprobación de que ninguna petición lleva el token en la URL.
- API de control para tests: `server.CreateRepo(...)`, `server.CommitFiles(owner, repo, branch, files, message)` (simula ediciones desde la web/otro cliente), `server.ForcePush(...)`, `server.GetFiles(...)`.

## 15. Seguridad y privacidad

- Solo HTTPS; validación de certificados por defecto (sin desactivación).
- Tokens solo en el almacén seguro del SO; en memoria el mínimo tiempo posible.
- Los repos se crean **privados por defecto**.
- Logs: sin contenido de notas; las rutas de ficheros se registran solo en nivel `Debug`.
- Opción "Cerrar sesión" que revoca el token (`DELETE /applications/{client_id}/token` no es posible sin secreto de cliente → se elimina localmente y se muestra enlace a la página de autorizaciones de GitHub).
