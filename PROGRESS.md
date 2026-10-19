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
- [ ] T1.2 Value objects de vault y nota
- [ ] T1.3 Modelo de enlaces
- [ ] T1.4 LinkResolver
- [ ] T1.5 Generación de enlaces
- [ ] T1.6 LinkRewriter
- [ ] T1.7 LineDiff (Myers)
- [ ] T1.8 ThreeWayTextMerger (diff3)
- [ ] T1.9 Fusión estructural
- [ ] T1.10 Plantillas y notas diarias
- [ ] T1.11 FuzzyMatcher

## Fase 2 — Markdown y sistema de ficheros

- [ ] T2.1 IMarkdownAnalyzer con Markdig
- [ ] T2.2 IFrontmatterEditor
- [ ] T2.3 IMarkdownRenderer → RenderDocument
- [ ] T2.4 Puerto IVaultFileSystem + InMemoryVaultFileSystem + VaultFileSystemContract
- [ ] T2.5 PhysicalVaultFileSystem
- [ ] T2.6 PhysicalFileWatcher
- [ ] T2.7 Almacenes JSON

## Fase 3 — Aplicación: vaults, notas e índice

- [ ] T3.1 Infraestructura de aplicación
- [ ] T3.2 Casos de uso de vaults
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
