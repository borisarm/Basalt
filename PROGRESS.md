# PROGRESS

Registro de avance del plan (`docs/specs/10-plan-de-implementacion.md`). Plataforma de desarrollo: Windows (ver ADR-0008).


## Fase 0 — Cimientos

- [x] T0.1 Estructura de la solución — 2026-10-19. SDK 11 RC + TFM net10.0 (ADR-0008); Verify sustituido por snapshots propios (ADR-0009); ADRs movidos a docs/specs/adr; proyecto extra `Basalt.Adapters.Platform.Tests`; `Basalt.iOS` se crea en T0.4/T10.1. Verificado solo en Windows (macOS en CI).
- [x] T0.2 Tests de arquitectura — 2026-10-19. R1–R7, matriz de módulos (ArchUnitNET), escáner IL de APIs prohibidas y convenciones, con tests canario por regla. El test DI y "puerto sin fake/contrato" se añaden cuando existan (T6.1 / Fase 2).
- [x] T0.3 CI — 2026-10-19. ci.yml (matriz Windows/macOS/Linux, UI, simulación, coverage-gate con build/coverage-gate.py, arquitectura, iOS condicionado a T10.1, seguridad, formato) y nightly.yml (Stryker, simulación 10 000, Live gated, benchmarks).
- [ ] T0.4 Spike de plataforma (iOS + AvaloniaEdit) — **PARCIAL / BLOQUEADO (requiere macOS)**: ADR-0007 aceptado (headless OK con xUnit v3 vía `Avalonia.Headless`); AvaloniaEdit 12 verificado en headless; validación en simulador iOS pendiente → ADR-0006 provisional (TextBox en iOS). El head iOS se crea en T10.1.
- [x] T0.5 Basalt.Testing base — 2026-10-19. Result/Result<T>/Error/Unit en Domain.Common (cobertura 100 %, mutación 100 %); Basalt.Testing con TempDirectory, Snapshot (ADR-0009), A, ContractTest<T>. Stryker funciona con el runner `mtp` (stryker-config.json). FakeClock se crea con el puerto IClock (T3.1).
- [x] T0.6 Trazabilidad — 2026-10-19. SpecTraceabilityTests en modo informe (escanea F-x.y, S01–S32, SYNC-ROW-n, SYNC-P1..5 y NFR automatizables frente a [Trait("Spec")] en el código de tests). Estricto con BASALT_TRACE_STRICT=1; se activará por defecto en T9.10.

## Fase 1 — Dominio núcleo

- [x] T1.1 VaultPath y clasificación de ficheros — 2026-10-19. VaultPath canónica (NFC, separadores, '..', nombres reservados de Windows, 255 bytes UTF-8), CaseFoldKey, FileClassifier; ~110 casos de tabla + propiedades FsCheck (Arb.VaultPath, BasaltProperty). Cobertura 100 % líneas, mutación 96,8 %.
- [x] T1.2 Value objects de vault y nota — 2026-10-19. VaultId, VaultName, VaultSettings (+LinkFormat/LinkPathStyle), NoteTitle, NoteContent (fin de línea/BOM, WithText preserva convención), SourceSpan, Heading (slug 05 §3.2), BlockId, Tag (insensible a mayúsculas) y TagHierarchy (conteo agregado, F-006.1). Mutación ~94 %.
- [x] T1.3 Modelo de enlaces — 2026-10-19. Link, LinkKind, SubpathKind + WikiLinkSyntax (gramática 05 §2: subpath encabezado/bloque, alias, '\|' en tablas, spans de destino para reescritura). Mutación 93 % (supervivientes equivalentes).
- [x] T1.4 LinkResolver — 2026-10-19. LinkResolver (05 §3: nombre insensible a mayúsculas, absoluta, sufijo, relativa ./ ../, enlaces Markdown relativa→absoluta→nombre con %-decoding, esquemas externos, desempates exacto/misma carpeta/más corta/ordinal, Ambiguous) + IVaultFileCatalog e InMemoryFileCatalog. ~75 casos LINK-3.x; mutación 93 %.
- [x] T1.5 Generación de enlaces — 2026-10-19. LinkFormatter: Shortest (nombre si único, si no el sufijo más corto que resuelve de vuelta — usa LinkResolver como oráculo), Relative (./ y ../ en wikilinks), Absolute × Wikilink/Markdown (%20, .md). Test de ida y vuelta. Mutación 92 %. Spec 09 §8: Links puede depender de Vaults (VaultSettings).
- [x] T1.6 LinkRewriter — 2026-10-19. LinkRewriter: reescribe solo TargetSpan de enlaces cuya resolución tras la operación ya no apunta al destino (renombrado), cubre ambigüedades nuevas y enlaces relativos de notas movidas; preserva sintaxis, estilo relativo/ruta completa y .md explícito. F-004.4/.5/.7 a nivel de dominio; propiedades: texto fuera de enlaces intacto, idempotencia, destinos correctos. Mutación 88,6 %.
- [x] T1.7 LineDiff (Myers) — 2026-10-19. LineDiff (Myers O(ND) en espacio lineal por bisección, ids enteros por línea, opción de ignorar fin de línea), DiffHunk, TextLines (split con terminadores). Propiedades: Apply(a, Diff(a,b)) == b y minimalidad frente a LCS por programación dinámica. Mutación 100 %.
- [x] T1.8 ThreeWayTextMerger (diff3) — 2026-10-19. ThreeWayTextMerger diff3 por líneas: un lado → ese lado, cambios idénticos una vez, solapes/inserciones en el mismo punto → conflicto con marcadores configurables; zonas estables desde ours, CRLF/LF ignorado al comparar y fin de línea predominante de ours. 30+ casos + propiedades (identidades, inserciones conservadas). Mutación 100 %. El caso especial de frontmatter se conecta en T1.9.
- [x] T1.9 Fusión estructural — 2026-10-19. StructuredValue (Common; valores YAML/JSON ya analizados, igualdad profunda, orden de claves), FrontmatterMerger (nivel de clave: conflicto si la misma clave cambia distinto en ambos lados) y JsonStructuralMerger (recursivo por clave y por elemento de lista). La conexión con ThreeWayTextMerger se hace en Aplicación (PlanMaterializer, T9.3) porque el análisis YAML vive en el adaptador. Mutación 100 %.
- [x] T1.10 Plantillas y notas diarias — 2026-10-19. MomentFormat (formateador propio + ToDotNet; YYYY…ss, Do, Q, A/a, [literal]), TemplateRenderer ({{title}}, {{date[:F]}}, {{time[:F]}}), DailyNotePolicy (carpeta/formato con subcarpetas). Tablas en-US y es-ES (F-009.1 'lunes 19 octubre 2026'). DailyNotePolicy recibe carpeta y formato (no VaultSettings) para respetar la matriz de módulos. Mutación 100 %.
- [x] T1.11 FuzzyMatcher — 2026-10-19. FuzzyMatcher estilo fzy (alineación óptima por DP, bonificaciones de inicio de palabra/consecutivos/mayúsculas, plegado de mayúsculas y diacríticos, posiciones) + Rank con desempates. Propiedades: exacta ≥ parcial, posiciones crecientes, coincide ⇔ subsecuencia. Mutación 100 %.

## Fase 2 — Markdown y sistema de ficheros

- [x] T2.1 IMarkdownAnalyzer con Markdig — 2026-10-19. MarkdigAnalyzer: parsers propios de wikilinks/embeds (gramática del dominio), etiquetas, comentarios %% en línea y en bloque; frontmatter seguro con YamlDotNet (≤64 KB, sin tipos, inferencia de tipos), enlaces de frontmatter, enlaces Markdown con %-encoding/<>, ids de bloque (párrafo, lista, tabla, línea propia), tareas, alias, recuento de palabras (CJK). 70 golden files revisados uno a uno (Snapshot.MatchFile). Rendimiento 1 MB comprobado. Nuevo módulo de dominio Metadata (ADR-0010). Cobertura del adaptador 92 %.
- [x] T2.2 IFrontmatterEditor — 2026-10-19. YamlFrontmatterEditor: edición quirúrgica por rango de líneas de la clave (escalares y listas, conserva estilo flow/compacto/indentado y CRLF), regeneración canónica para mapas anidados, alta antes del cierre, creación de frontmatter, YAML inválido nunca se reescribe; YamlEmitter decide comillas por ida y vuelta con el parser seguro. F-010.1 + snapshots.
- [x] T2.3 IMarkdownRenderer → RenderDocument — 2026-10-19. IMarkdownRenderer → RenderDocument (bloques: encabezado, párrafo, lista/tareas, cita, callout con tipos/alias/plegado, código/math/html como texto, tabla, imagen, embed con tamaño, separador, notas al pie; inlines con enlaces internos/externos, embeds, etiquetas, resaltado, tachado). Ids de bloque separados del texto; frontmatter vacío reconocido; delimitadores sin cerrar literales; etiquetas con marcas combinantes (spec 05 §4 actualizada). Snapshots de render de los 70 golden revisados.
- [x] T2.4 Puerto IVaultFileSystem + InMemoryVaultFileSystem + VaultFileSystemContract — 2026-10-19. Puerto IVaultFileSystem (lector/escritor/enumerador/watcher segregados + CreateNewAsync para creaciones 'no debe existir'), VaultFileConventions (temporales .~basalt-*.tmp), InMemoryVaultFileSystem (insensible a mayúsculas por defecto, FailNextWrite, cambios externos simulados → ManualFileWatcher) y VaultFileSystemContract (26 casos). BlobSha/TreeSha/CommitSha y GitBlobHasher (con streaming) adelantados de T7.1.
- [x] T2.5 PhysicalVaultFileSystem — 2026-10-19. PhysicalVaultFileSystem: escritura atómica (temporal + flush a disco + rename), expectedSha, CreateNew sin sobrescribir (también en carrera), renombrado solo de mayúsculas vía nombre intermedio, nombre existente conservado en volúmenes insensibles, resolución NFC de nombres NFD en disco, sondeo de sensibilidad a mayúsculas, mapeo de excepciones a errores, enumeración sin temporales ni nombres no direccionables. Contrato completo en disco real + fallo inyectado entre temporal y rename (NFR-021). Cobertura 92 %.
- [x] T2.6 PhysicalFileWatcher — 2026-10-19. PhysicalFileWatcher (FileSystemWatcher, debounce 200 ms con TimeProvider, overflow → RescanRequired) + ChangeCoalescer puro (escritura atómica → Changed, creado+borrado se anula, renombrados, temporales filtrados). FileWatcherContract contra el fake y el real (crear/modificar/borrar/renombrar/temporales) + ráfaga de 1 000 ficheros. Estable en 4 ejecuciones seguidas.
- [x] T2.7 Almacenes JSON — 2026-10-19. IDocumentStore<T> (puerto) con JsonFileStore<T> (schemaVersion, migraciones, escritura atómica, lectura tolerante con copia .corrupt) e InMemoryStore<T> bajo KeyValueStoreContract; VaultRegistry/AccountStore/AppSettingsStore en Aplicación; JsonVaultConfigStore (.basalt/vault.json indentado) bajo VaultConfigStoreContract; dominio Accounts (GitHubAccount, GitHubHost); DesktopPlatformPaths y registro DI. ADR-0011.

## Fase 3 — Aplicación: vaults, notas e índice

- [x] T3.1 Infraestructura de aplicación — 2026-10-19. ICommand/IQuery + handlers, IValidator, decoradores Logging (sin datos de la petición)/Validation/VaultLock; AsyncReaderWriterLock (preferencia de escritores, cancelable) y VaultLocks; InMemoryEventBus (cola ordenada por vault, aislamiento de suscriptores, WaitForIdleAsync); VaultSession/IVaultSessions (ADR-0012); puertos IClock/IIdGenerator/IRandom/IDeviceInfo con implementaciones de sistema y fakes.
- [x] T3.2 Casos de uso de vaults — 2026-10-19. CreateVault, OpenVault (sesión + inicializadores IVaultSessionInitializer, vault.json ilegible → ajustes por defecto sin sobrescribir, ausente → recreado con el id registrado), OpenFolderAsVault (conserva VaultId ajeno, sigue carpetas movidas, vault.json ilegible → vault.config_invalid), CloseVault, RemoveVaultFromList (borra datos locales, nunca ficheros), ListVaults, UpdateVaultSettings + eventos. Puertos: IVaultFileSystemFactory.CreateAsync/Combine/NameOf, IVaultLocalDataStore. F-001.1–F-001.6 con fakes + integración en disco real. Cobertura Aplicación 95 %.
- [ ] T3.3 Puerto de índice + InMemoryNoteIndex + NoteIndexContract
- [ ] T3.4 SqliteNoteIndex
- [ ] T3.5 IMetadataCache + indexación incremental al abrir y en caliente
- [ ] T3.6 tools/Basalt.VaultGenerator
- [ ] T3.7 Casos de uso de ficheros

## Fase 4 — Aplicación: conocimiento y búsqueda

- [ ] T4.1 SearchQueryParser + AST
- [ ] T4.2 INoteMatcher
- [ ] T4.3 Búsqueda en SQLite
- [ ] T4.4 Backlinks, salientes y menciones no enlazadas
- [ ] T4.5 Etiquetas
- [ ] T4.6 Grafo
- [ ] T4.7 Notas diarias, plantillas, marcadores, recientes, esquema, espacio de trabajo
- [ ] T4.8 ICommandRegistry y comandos núcleo

## Fase 5 — Presentación (ViewModels)

- [ ] T5.1 Base
- [ ] T5.2 VaultPickerViewModel, ShellViewModel
- [ ] T5.3 WorkspaceViewModel, PaneLayoutViewModel, TabGroupViewModel, NoteTabViewModel
- [ ] T5.4 EditorCommandsService
- [ ] T5.5 Paneles
- [ ] T5.6 Overlays
- [ ] T5.7 Ajustes

## Fase 6 — UI de escritorio (Avalonia)

- [ ] T6.1 Basalt.UI base
- [ ] T6.2 Shell y vault picker; ventana por vault; menús nativos macOS
- [ ] T6.3 Layout de workspace
- [ ] T6.4 Editor de escritorio
- [ ] T6.5 Modo lectura
- [ ] T6.6 Paneles y overlays
- [ ] T6.7 Grafo
- [ ] T6.8 Ajustes y atajos
- [ ] T6.9 Tests UI headless

## Fase 7 — Dominio de sincronización

- [ ] T7.1 BlobSha/TreeSha/CommitSha, GitBlobHasher (incl. streaming), GitTreeHasher, GitCommitHasher
- [ ] T7.2 Manifest, ManifestEntry, ChangeSet, SyncBase
- [ ] T7.3 IgnoreRules
- [ ] T7.4 ConflictCopyNamer, clasificación texto/binario
- [ ] T7.5 SyncPlanner

## Fase 8 — Adaptador GitHub y autenticación

- [ ] T8.1 Basalt.Testing.FakeGitHub
- [ ] T8.2 InMemoryRemoteRepository + RemoteRepositoryContract
- [ ] T8.3 GitHubApiClient
- [ ] T8.4 GitHubRemoteRepository
- [ ] T8.5 IRepositoryAdministration
- [ ] T8.6 GitHubDeviceFlowAuthenticator
- [ ] T8.7 Almacenes de secretos
- [ ] T8.8 Basalt.Adapters.GitHub.LiveTests

## Fase 9 — Motor y UX de sincronización

- [ ] T9.1 LocalSnapshotScanner + hash cache
- [ ] T9.2 RemoteSnapshotProvider
- [ ] T9.3 PlanMaterializer
- [ ] T9.4 RemotePublisher
- [ ] T9.5 LocalApplier
- [ ] T9.6 SyncStateCommitter + ISyncStateStore JSON + journal + log
- [ ] T9.7 VaultSyncEngine
- [ ] T9.8 SyncCoordinator
- [ ] T9.9 Casos de uso de cuentas y sync
- [ ] T9.10 Basalt.Sync.IntegrationTests
- [ ] T9.11 ViewModels y vistas de sync

## Fase 10 — iOS

- [ ] T10.1 Head iOS
- [ ] T10.2 Vistas iPhone
- [ ] T10.3 Ciclo de vida y sync en segundo plano
- [ ] T10.4 Tests

## Fase 11 — Calidad, rendimiento y release

- [ ] T11.1 Benchmarks
- [ ] T11.2 Accesibilidad
- [ ] T11.3 Seguridad
- [ ] T11.4 Empaquetado y firma
- [ ] T11.5 docs/release-checklist.md
- [ ] T11.6 (Opcional) Live Preview
