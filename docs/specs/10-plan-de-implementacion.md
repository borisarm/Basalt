# 10 — Plan de implementación

El plan está ordenado para que cada tarea se apoye solo en las anteriores, se pueda completar en una sesión de trabajo del agente y deje el repositorio **en verde** (compila, todos los tests pasan, umbrales cumplidos).

**Formato de cada tarea**: identificador `T<fase>.<n>`, objetivo, entregables, pruebas exigidas y criterio de aceptación. Las referencias `[§]` apuntan a la especificación que manda.

**Registro de progreso**: `PROGRESS.md` en la raíz, con una casilla por tarea (`- [ ] T1.3 …`), fecha de finalización y notas (desviaciones, ADRs creados). El agente lo actualiza al completar cada tarea.

## Definition of Done (aplica a todas las tareas)

1. Código implementado siguiendo [01](01-arquitectura.md) y [13](13-convenciones-de-codigo.md).
2. Tests escritos **antes** del código (TDD) cubriendo: todos los escenarios/reglas de la especificación referenciados por la tarea (con `[Trait("Spec", …)]`), casos de error, límites; unitarios **y** de integración cuando la tarea toca un adaptador o un flujo entre capas.
3. `dotnet build` sin advertencias; `dotnet test` en verde; `dotnet format --verify-no-changes` limpio.
4. Umbrales de cobertura de [09](09-estrategia-de-pruebas.md) §10 cumplidos para los proyectos tocados.
5. Si toca `Basalt.Domain`: Stryker local sobre los ficheros cambiados ≥ umbral.
6. Tests de arquitectura en verde.
7. `PROGRESS.md` actualizado; commit con Conventional Commits.
8. Si se tomó una decisión no especificada: ADR nuevo en `docs/specs/adr/`.

---

## Fase 0 — Cimientos

**T0.1 Estructura de la solución.** Crear `global.json`, `Basalt.slnx`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `BannedSymbols.txt`, `.gitignore`, `.config/dotnet-tools.json` (stryker, reportgenerator), todos los proyectos de [01](01-arquitectura.md) §2 vacíos con sus referencias permitidas, `PROGRESS.md`. Registrar versiones elegidas en `adr/0001-stack.md`.
*Aceptación*: `dotnet build` y `dotnet test` (sin tests aún, o un test trivial por proyecto) pasan en Windows y macOS.

**T0.2 Tests de arquitectura.** Implementar reglas R1–R7 y la matriz de módulos (aunque los módulos estén vacíos, las reglas deben poder fallar: incluir un test "canario" que verifique que la regla detecta una violación en un ensamblado de prueba).
*Aceptación*: añadir temporalmente una referencia prohibida hace fallar el test.

**T0.3 CI.** Workflows `ci.yml` y `nightly.yml` de [12](12-plataformas-build-ci.md) §4 (los jobs de proyectos aún vacíos pasan trivialmente). Puerta de cobertura con ReportGenerator.

**T0.4 Spike de plataforma (iOS + AvaloniaEdit).** App mínima `Basalt.iOS` con una vista que contiene `AvaloniaEdit.TextEditor`; verificar en simulador: teclado, IME (acentos, emoji), selección, scroll. Documentar resultado en `adr/0006-editor-ios.md` y decidir editor de iOS ([07](07-interfaz-usuario.md) §5.1). Verificar también compatibilidad de Avalonia.Headless con xUnit v3 (`adr/0007-ui-tests.md`).
*Aceptación*: ADRs escritos; head iOS compila en CI.

**T0.5 `Basalt.Testing` base.** `Result`, `Error` (en Domain), `FakeClock`/uso de `FakeTimeProvider`, `TempDirectory`, builder base `A`, estructura de `Contracts/`.
*Pruebas*: tests unitarios de `Result<T>` (Map/Bind/errores).

**T0.6 Trazabilidad.** `SpecTraceabilityTests` en modo "informe" (no falla) hasta la Fase 9; a partir de T9.10 pasa a modo estricto.

## Fase 1 — Dominio núcleo

**T1.1 `VaultPath` y clasificación de ficheros** [02 §3]. Todas las reglas de validación y normalización, `CaseFoldKey`.
*Pruebas*: tablas de válidos/inválidos (≥ 60), NFC/NFD, nombres reservados de Windows, round-trip (property).

**T1.2 Value objects de vault y nota** [02 §2, §4]: `VaultId`, `VaultName`, `NoteTitle`, `NoteContent` (detección de fin de línea y BOM), `SourceSpan`, `Heading`, `BlockId`, `Tag` + `TagHierarchy` [02 §6].

**T1.3 Modelo de enlaces** [02 §5, 05 §2]: `Link`, `LinkKind`, `SubpathKind`.

**T1.4 `LinkResolver`** [05 §3] con `IVaultFileCatalog` (interfaz de dominio) y un catálogo en memoria para tests.
*Pruebas*: ≥ 40 casos (`[Trait("Spec","LINK-3.x")]`), ambigüedades, relativos, sufijos, mayúsculas.

**T1.5 Generación de enlaces** [05 §3.1]: `LinkFormatter` (Shortest/Relative/Absolute × Wikilink/Markdown, codificación `%20`).

**T1.6 `LinkRewriter`** [02 §5.2]. *Pruebas*: tablas + properties (texto fuera de spans intacto, idempotencia).

**T1.7 `LineDiff` (Myers)** [02 §10]. *Pruebas*: casos clásicos + property `Apply(a, Diff(a,b)) == b`.

**T1.8 `ThreeWayTextMerger` (diff3)** [02 §10]. *Pruebas*: ≥ 30 casos + propiedades de identidad.

**T1.9 Fusión estructural** de frontmatter y JSON (`IFrontmatterMerger`, `IJsonStructuralMerger`) a nivel de clave/elemento sobre valores ya analizados.

**T1.10 Plantillas y notas diarias** [02 §9]: `MomentFormat`, `TemplateRenderer`, `DailyNotePolicy`. *Pruebas*: tablas con culturas `en-US` y `es-ES`.

**T1.11 `FuzzyMatcher`** [02 §7]. *Pruebas*: orden esperado + propiedades.

## Fase 2 — Markdown y sistema de ficheros

**T2.1 `IMarkdownAnalyzer` con Markdig** [05]. Extensiones de wikilink, embed, tag, block id, highlight, comentario, callout. *Pruebas*: golden files (≥ 60) con Verify; rendimiento 1 MB < 50 ms.

**T2.2 `IFrontmatterEditor`** [05 §5]. *Pruebas*: snapshots de edición quirúrgica y regeneración.

**T2.3 `IMarkdownRenderer` → `RenderDocument`** [07 §6]. *Pruebas*: snapshots del modelo de render para los golden files.

**T2.4 Puerto `IVaultFileSystem` + `InMemoryVaultFileSystem` + `VaultFileSystemContract`** [03 §1.1].

**T2.5 `PhysicalVaultFileSystem`** con escritura atómica, `expectedSha`, renombrado de mayúsculas, NFC. *Pruebas*: contrato en disco real + inyección de fallos de escritura.

**T2.6 `PhysicalFileWatcher`** con debounce, filtrado de temporales y rescan en overflow. *Pruebas*: `FileWatcherContract` (integración, con timeouts generosos).

**T2.7 Almacenes JSON** (`JsonFileStore<T>`, `IVaultRegistry`, `IAppSettingsStore`, `IAccountStore`, `IVaultConfigStore`) con `schemaVersion` y migraciones [08 §1.1]. *Pruebas*: `KeyValueStoreContract` + lectura tolerante.

## Fase 3 — Aplicación: vaults, notas e índice

**T3.1 Infraestructura de aplicación**: `ICommandHandler`/`IQueryHandler`, decoradores (logging, validación, `VaultLockDecorator`), `InMemoryEventBus`, `IVaultScopeFactory`/`VaultSession` [01 §4].

**T3.2 Casos de uso de vaults** (F-001): `CreateVault`, `OpenVault`, `OpenFolderAsVault`, `CloseVault`, `RemoveVaultFromList`, `ListVaults`, `UpdateVaultSettings`. *Pruebas*: F-001.1–F-001.6 con fakes + integración con FS real.

**T3.3 Puerto de índice + `InMemoryNoteIndex` + `NoteIndexContract`** [03 §1.3].

**T3.4 `SqliteNoteIndex`** [08 §2]: esquema, migraciones, upsert/remove/rename. *Pruebas*: contrato + corrupción + concurrencia.

**T3.5 `IMetadataCache` + indexación incremental al abrir y en caliente** [08 §2.2, §3]. *Pruebas*: property "incremental ≡ reconstruida"; NFR-002 (rendimiento) con vault generado.

**T3.6 `tools/Basalt.VaultGenerator`** (vault de referencia determinista) [11].

**T3.7 Casos de uso de ficheros** (F-002): crear nota/carpeta (nombres únicos), leer, guardar con concurrencia optimista, renombrar/mover **con actualización de enlaces**, eliminar (papelera), árbol de ficheros, importar adjunto, propiedades, alternar tarea. *Pruebas*: F-002.1–F-002.7, F-004.4–F-004.7, F-010.1–F-010.2, F-011.1 (a nivel de aplicación).

## Fase 4 — Aplicación: conocimiento y búsqueda

**T4.1 `SearchQueryParser` + AST** [08 §4.1] (dominio). *Pruebas*: ≥ 60 consultas con snapshots, precedencia OR.

**T4.2 `INoteMatcher`** [08 §4.2] (dominio). *Pruebas*: tabla por operador.

**T4.3 Búsqueda en SQLite** (`FtsQueryBuilder` + post-filtro) [08 §4.3]. *Pruebas*: equivalencia con oráculo (property), F-007.1–F-007.5.

**T4.4 Backlinks, salientes y menciones no enlazadas** (F-005), `LinkUnlinkedMention`. *Pruebas*: F-005.1–F-005.3.

**T4.5 Etiquetas** (F-006), autocompletado de enlaces/etiquetas (F-004.1–F-004.3), quick switcher (F-008.1).

**T4.6 Grafo**: `GraphBuilder`, filtros, grafo local, `ForceLayout` con Barnes-Hut (dominio) (F-012). *Pruebas*: F-012.1–F-012.2, determinismo.

**T4.7 Notas diarias, plantillas, marcadores, recientes, esquema, espacio de trabajo** (F-009, F-011, F-013 lógica). *Pruebas*: F-009.1–F-009.3.

**T4.8 `ICommandRegistry` y comandos núcleo** [07 §9]. *Pruebas*: F-008.2 (a nivel de registro).

## Fase 5 — Presentación (ViewModels)

**T5.1 Base**: `ViewModelBase`, `IUiDispatcher`, `IDialogService`, `INotificationService`, `ILocalizer` + recursos `en`/`es`, `IWorkspaceNavigator`, `IWorkspaceMessenger`. Test de que todas las claves existen en ambos idiomas.

**T5.2 `VaultPickerViewModel`, `ShellViewModel`.**

**T5.3 `WorkspaceViewModel`, `PaneLayoutViewModel`, `TabGroupViewModel`, `NoteTabViewModel`** (autoguardado con `TimeProvider`, recarga externa F-003.2, fusión F-003.3).

**T5.4 `EditorCommandsService`** [07 §5.2] (≥ 80 casos tabulares; F-003.4).

**T5.5 Paneles**: explorador, búsqueda, marcadores, etiquetas, backlinks, salientes, esquema, propiedades, grafo local.

**T5.6 Overlays**: quick switcher, paleta de comandos; `KeyBindingService`.

**T5.7 Ajustes** (F-014) excepto cuentas y sync.

## Fase 6 — UI de escritorio (Avalonia)

**T6.1 `Basalt.UI` base**: `App.axaml`, tema y tokens (claro/oscuro), `AvaloniaUiDispatcher`, `DialogService`, extensión `Loc`. `Basalt.Desktop` con raíz de composición. Test DI de [09](09-estrategia-de-pruebas.md) §8.

**T6.2 Shell y vault picker; ventana por vault; menús nativos macOS.**

**T6.3 Layout de workspace**: ribbon, sidebars redimensionables, pestañas, splits, barra de estado.

**T6.4 Editor de escritorio** (`MarkdownEditorControl`): TextMate + gramática propia, transformadores, plegado, autocompletado, puente con `NoteTabViewModel`, barra de búsqueda/reemplazo, pegado de imágenes (F-003.5).

**T6.5 Modo lectura** (`RenderDocumentView`), embeds, imágenes con caché, tareas clicables.

**T6.6 Paneles y overlays** (vistas de T5.5–T5.6).

**T6.7 Grafo** (`GraphCanvas`) global y local.

**T6.8 Ajustes y atajos** (vistas).

**T6.9 Tests UI headless** de [09](09-estrategia-de-pruebas.md) §5.5 y snapshots de 5 vistas en ambos temas.

*Aceptación de fase*: la app de escritorio es utilizable de extremo a extremo sin sincronización (F-001…F-014) en Windows y macOS.

## Fase 7 — Dominio de sincronización

**T7.1 `BlobSha`/`TreeSha`/`CommitSha`, `GitBlobHasher` (incl. streaming), `GitTreeHasher`, `GitCommitHasher`** [06 §9.4]. *Pruebas*: vectores conocidos.

**T7.2 `Manifest`, `ManifestEntry`, `ChangeSet`, `SyncBase`.**

**T7.3 `IgnoreRules`** (gitignore) [06 §3]. *Pruebas*: ≥ 50 casos con datos de `git check-ignore`.

**T7.4 `ConflictCopyNamer`, clasificación texto/binario** [06 §5].

**T7.5 `SyncPlanner`** [06 §5]: tabla completa, políticas, conflictos fichero/carpeta, cuarentena. *Pruebas*: `SYNC-ROW-1..15` × políticas; propiedades `SYNC-P1..P5`.

## Fase 8 — Adaptador GitHub y autenticación

**T8.1 `Basalt.Testing.FakeGitHub`** [06 §14]: almacén de objetos Git, endpoints Git Data, refs con FF, repos vacíos, permisos, ETag, rate limit, truncated, zipball, paginación, device flow, `FaultPlan`, API de control. *Pruebas propias*: el fake se valida comparando SHAs con vectores reales y con `InMemoryRemoteRepository`.

**T8.2 `InMemoryRemoteRepository` + `RemoteRepositoryContract`.**

**T8.3 `GitHubApiClient`**: handlers (auth, redacción de logs), resiliencia, rate limit, ETag, paginación, mapeo de errores, JSON source-gen. *Pruebas*: contra FakeGitHub por HTTP, cada código de error.

**T8.4 `GitHubRemoteRepository`** (todas las operaciones de `IRemoteRepository`, troceado de create-tree, contenido inline, zipball con protección zip slip). *Pruebas*: `RemoteRepositoryContract` contra FakeGitHub.

**T8.5 `IRepositoryAdministration`** (listar, crear, obtener info, crear rama).

**T8.6 `GitHubDeviceFlowAuthenticator`**, `IGitHubTokenProvider` (refresco, 401). *Pruebas*: `AuthenticatorContract` contra FakeGitHub (pending, slow_down, denied, expired).

**T8.7 Almacenes de secretos**: `WindowsCredentialStore` (+ DPAPI fallback), `AppleKeychainStore` (macOS e iOS). *Pruebas*: `SecretStoreContract` por SO.

**T8.8 `Basalt.Adapters.GitHub.LiveTests`** (opcionales, gated).

## Fase 9 — Motor y UX de sincronización

**T9.1 `LocalSnapshotScanner` + hash cache.** *Pruebas*: NFR-010.

**T9.2 `RemoteSnapshotProvider`** (ETag, caché de árboles, cuarentena).

**T9.3 `PlanMaterializer`** (staging, verificación SHA, fusiones, zipball > 200).

**T9.4 `RemotePublisher`** (inline/blobs, troceado, verificación de tree SHA, bucle non-FF, repo vacío).

**T9.5 `LocalApplier`** (concurrencia optimista, aplazados, moves, carpetas vacías, eventos).

**T9.6 `SyncStateCommitter` + `ISyncStateStore` JSON + journal + log.**

**T9.7 `VaultSyncEngine`** (orquestación pasos 0–10) — tests de aplicación con fakes de todos los casos de [06](06-sincronizacion-github.md) §6–§7, incluida cancelación en cada paso.

**T9.8 `SyncCoordinator`** (disparadores, coalescencia, backoff, paralelismo, aislamiento) con `FakeTimeProvider`.

**T9.9 Casos de uso de cuentas y sync** ([03](03-puertos-y-adaptadores.md) §2): sign-in, configure (dry-run + modos de vinculación §10), clonar, pausar, deshabilitar, estado, log, conflictos, historial, restaurar.

**T9.10 `Basalt.Sync.IntegrationTests`**: escenarios S01–S32 y simulación aleatoria [09 §7]. Activar trazabilidad estricta.

**T9.11 ViewModels y vistas de sync**: `SyncStatusViewModel`, `GitHubSignInViewModel`, `ConfigureSyncWizardViewModel`, `AccountsSettingsVM`, `VaultSyncSettingsVM`, centro de conflictos con `DiffView`, historial de versiones (F-015.1–F-015.7). Cierre de app con sync pendiente.

*Aceptación de fase*: dos instancias de escritorio (Windows y macOS) sincronizan un vault real contra github.com sin pérdidas en una prueba manual guiada (documentada en `docs/release-checklist.md`).

## Fase 10 — iOS

**T10.1 Head iOS**: `AppDelegate`, `ISingleViewApplicationLifetime`, raíz de composición iOS, `IPlatformPaths`, Keychain, `IConnectivityMonitor` (NWPathMonitor), Info.plist [12 §3].

**T10.2 Vistas iPhone** (navegación en pila, barra inferior, hojas) e **iPad** (columnas) [07 §4]; editor según ADR-0006; barra de accesorios del teclado.

**T10.3 Ciclo de vida y sync en segundo plano**: `beginBackgroundTask` al pasar a segundo plano, `BGAppRefreshTask` (`IBackgroundSyncScheduler`), solo Wi-Fi opcional.

**T10.4 Tests**: ViewModels específicos de iOS (si los hay), tests de composición DI del head iOS, build de CI; lista de verificación manual en dispositivo.

## Fase 11 — Calidad, rendimiento y release

**T11.1 Benchmarks** (`Basalt.Benchmarks`) y tests de rendimiento para todos los NFR automatizables; optimizar hasta cumplir.

**T11.2 Accesibilidad**: nombres de automatización, navegación por teclado, test de contraste de tokens (NFR-040…044).

**T11.3 Seguridad**: test de búsqueda de token (NFR-030), revisión de dependencias, límites de YAML/zip.

**T11.4 Empaquetado y firma** de las tres plataformas; `release.yml`.

**T11.5 `docs/release-checklist.md`** y guía de usuario mínima (`docs/user-guide.md`: crear vault, conectar GitHub, conflictos).

**T11.6 (Opcional) Live Preview**: ocultación de sintaxis en líneas no activas en el editor de escritorio.

---

## Orden de dependencias resumido

```
F0 ─▶ F1 ─▶ F2 ─▶ F3 ─▶ F4 ─▶ F5 ─▶ F6
                    │                 │
                    └──▶ F7 ─▶ F8 ─▶ F9 ─▶ F10 ─▶ F11
```
F7–F8 pueden desarrollarse en paralelo con F5–F6 (no comparten ficheros), lo que permite trabajar en dos ramas/sesiones simultáneas si se desea.
