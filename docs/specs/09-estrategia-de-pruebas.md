# 09 — Estrategia de pruebas

Las pruebas son un **entregable de primera clase**. Una tarea del plan no está terminada si no incluye sus pruebas unitarias **y** de integración según esta estrategia, y si los umbrales de §10 no se cumplen.

## 1. Proceso: TDD obligatorio

Para cada comportamiento:

1. **Rojo**: escribir el test (o tests) que expresa el comportamiento a partir de la especificación (escenario Gherkin, fila de tabla de decisión, regla). Ejecutarlo y **ver que falla por el motivo correcto**.
2. **Verde**: escribir el código mínimo para que pase.
3. **Refactor**: limpiar manteniendo verde; aplicar SOLID.
4. Repetir. Al terminar la tarea: suite completa, cobertura y (si toca dominio) mutación local del módulo afectado.

Los tests se escriben **contra el contrato** (puerto o API pública del componente), no contra detalles de implementación. Prohibido cambiar un test existente para que pase un cambio de comportamiento sin que la especificación lo justifique (y en ese caso se actualiza también la especificación).

## 2. Pirámide y tipos de prueba

| Nivel | Proyecto(s) | Qué prueba | Dobles | Velocidad objetivo |
|-------|-------------|-----------|--------|--------------------|
| Unitario de dominio | `Basalt.Domain.Tests` | Value objects, servicios puros, algoritmos | Ninguno (puro) | < 5 s la suite |
| Unitario de aplicación | `Basalt.Application.Tests` | Handlers, orquestación (SyncEngine, MetadataCache, Coordinator) | Fakes en memoria de `Basalt.Testing` (preferidos) y NSubstitute para verificar interacciones puntuales | < 15 s |
| Propiedades | En los dos anteriores (`[Property]`) | Invariantes algebraicos | — | Incluidas arriba (100 casos por defecto; 10 000 en nightly vía variable) |
| Contrato | `Basalt.Testing/Contracts` + proyectos de adaptador | Que fake y real cumplen el mismo puerto | — | — |
| Integración de adaptador | `Basalt.Adapters.*.Tests` | Adaptador real contra recurso real (disco, SQLite, HTTP a FakeGitHub) | Ninguno o FakeGitHub | < 60 s cada uno |
| Snapshot | `Basalt.Adapters.Markdown.Tests`, Presentation, UI | Salidas complejas (metadatos, modelo de render, capturas) | Helper propio (ADR-0009) | — |
| ViewModel | `Basalt.Presentation.Tests` | Lógica de presentación, comandos, estados | Fakes + `ImmediateDispatcher` | < 15 s |
| UI headless | `Basalt.UI.Tests` | Vistas Avalonia: bindings, interacción, atajos | Avalonia.Headless + ViewModels con fakes | < 90 s |
| Extremo a extremo de sync | `Basalt.Sync.IntegrationTests` | Varios "dispositivos" con FS real + composición real del núcleo + FakeGitHub por HTTP | FakeGitHub | < 5 min (PR) |
| Arquitectura | `Basalt.Architecture.Tests` | Reglas de dependencia, convenciones, DI, trazabilidad | — | < 20 s |
| Live (opcional) | `Basalt.Adapters.GitHub.LiveTests` | Contrato `IRemoteRepository` y device flow parcial contra github.com | — | Nightly |
| Rendimiento | `Basalt.Benchmarks` (BenchmarkDotNet) + tests `[Trait("Category","Performance")]` | NFR | — | Nightly |

## 3. Herramientas

- **xUnit v3** sobre Microsoft.Testing.Platform. `[Fact]`, `[Theory]` + `[InlineData]`/`[MemberData]`/`TheoryData<>` tipada.
- **AwesomeAssertions** (fork Apache-2.0 de FluentAssertions) para aserciones legibles. (No usar FluentAssertions ≥ 8 por licencia.)
- **NSubstitute** solo cuando se necesita verificar una interacción que no es observable mediante el estado de un fake.
- **FsCheck 3** (`FsCheck.Xunit.v3`) para property-based testing, con generadores propios en `Basalt.Testing/Generators` (`Arb.VaultPath`, `Arb.MarkdownDocument`, `Arb.Manifest`, `Arb.TextEdit`, `Arb.SearchQuery`, `Arb.DeviceOperation`).
- Snapshots con el helper propio `Snapshot.Match` de `Basalt.Testing` (`*.verified.*` versionados; ver ADR-0009, sustituye a Verify.XunitV3).
- **Bogus** para datos realistas (con semilla fija).
- **Microsoft.Extensions.TimeProvider.Testing** (`FakeTimeProvider`) para tiempo.
- **Avalonia.Headless** para UI con el helper `Headless.RunAsync` sobre xUnit v3 (ADR-0007: `Avalonia.Headless.XUnit` no es compatible con xUnit v3 4.x).
- **ArchUnitNET** (o NetArchTest) para arquitectura.
- **Cobertura**: `Microsoft.Testing.Extensions.CodeCoverage` (formato Cobertura) + ReportGenerator.
- **Stryker.NET** para mutación.

## 4. Utilidades compartidas (`Basalt.Testing`)

Se construyen en la Fase 0–2 y crecen con el proyecto:

- **Fakes** de todos los puertos de salida (ver [03](03-puertos-y-adaptadores.md) §3), deterministas, con inyección de fallos (`FailNext…`, `DelayNext…`) y registro de llamadas.
- **Builders**: `A.Vault()`, `A.Note()`, `A.Manifest()`, `A.SyncBase()`, `A.SyncProfile()`, `A.GitHubAccount()`, `A.Document().WithFrontmatter(...).WithLink(...)`.
- **`TestVault`**: crea un vault en disco temporal o en memoria a partir de un diccionario `ruta → contenido` y ofrece aserciones `ShouldContainFile`, `ShouldHaveContent`, `Snapshot()`.
- **`TestHost`**: compone el núcleo real (DI de producción) con adaptadores sustituibles: `TestHost.Create(b => b.UseInMemoryFileSystem().UseFakeGitHub(server).UseFakeClock())`. Es la base de los tests de aplicación de alto nivel y de sync.
- **Contratos** (`Contracts/`): clases abstractas `XxxContract` con `protected abstract Task<IXxx> CreateSutAsync()`; cada adaptador hereda en su proyecto de tests.

## 5. Qué DEBE probarse en cada capa (mínimos)

### 5.1 Dominio
- Cada value object: casos válidos, cada regla de invalidez (tabla), igualdad, normalización (NFC, separadores, mayúsculas).
- `LinkResolver`: todos los pasos de [05](05-markdown-y-enlaces.md) §3 con una `[Theory]` por paso y desempates; matriz de ≥ 40 casos.
- `LinkRewriter`: todos los formatos de enlace × estilos de ruta; property: texto fuera de `TargetSpan` intacto; idempotencia.
- `SearchQueryParser`: tabla de ≥ 60 consultas → AST esperado (snapshot), incluida precedencia y errores.
- `INoteMatcher`: semántica de cada operador.
- `FuzzyMatcher`: orden esperado en casos conocidos + propiedades.
- `LineDiff` y `ThreeWayTextMerger`: casos clásicos de diff3 (tests tomados de la literatura: inserciones adyacentes, borrados solapados, cambios idénticos, conflictos al inicio/fin, ficheros vacíos, sin salto final) + propiedades de [02](02-modelo-de-dominio.md) §10.
- `GitBlobHasher`/`GitTreeHasher`: vectores conocidos (p. ej. blob vacío = `e69de29bb2d1d6434b8b29ae775ad8c2e48c5391`, árbol vacío = `4b825dc642cb6eb9a060e54bf8d69288fbee4904`, y ≥ 10 vectores generados con `git hash-object`/`git mktree` incluidos como datos de test).
- `SyncPlanner`: **una prueba por cada fila** de la tabla de [06](06-sincronizacion-github.md) §5 × cada política aplicable, más las propiedades P1–P5.
- `IgnoreRules`: tabla de ≥ 50 casos compatibles con la semántica de gitignore (comparados con la salida de `git check-ignore` registrada como datos de test).
- `ConflictCopyNamer`, `DailyNotePolicy`, `TemplateRenderer`, `MomentFormat`: tablas.
- `ForceLayout`: determinismo con semilla; energía decreciente; estabilidad en grafos pequeños conocidos.

### 5.2 Aplicación
- Cada handler: camino feliz, cada `Error` documentado, eventos publicados, efectos sobre fakes, cancelación.
- `RenameEntryHandler`: reescritura de enlaces en todas las notas afectadas, en una operación por vault, con rollback lógico si una escritura falla (las ya escritas se revierten).
- `MetadataCache`: coherencia tras secuencias aleatorias de operaciones (property: la caché incremental == caché reconstruida desde cero).
- `VaultSyncEngine`: cada paso con fakes, incluyendo reintentos non-FF, aplazados por edición concurrente, cancelación en cada fase, repo vacío, truncated tree, zipball.
- `SyncCoordinator`: con `FakeTimeProvider`: intervalos, debounce, coalescencia, backoff y su reinicio, aislamiento entre vaults (un vault con error no retrasa a otro), límites de paralelismo.

### 5.3 Adaptadores (integración real)
- **FileSystem**: contrato completo en disco real; escritura atómica (simular fallo entre temp y rename); renombrado solo de mayúsculas en FS insensible (Windows/macOS en CI); NFC/NFD (macOS); watcher: crear/modificar/borrar/renombrar/ráfagas de 1 000 ficheros/overflow→rescan; ficheros bloqueados (Windows).
- **Markdown**: golden files ([05](05-markdown-y-enlaces.md) §8); `IFrontmatterEditor` preserva formato (snapshots).
- **Index**: contrato + equivalencia con `InMemoryNoteIndex` (property); migración de esquema; reindexado por cambio de `analyzer_version`; concurrencia lectura/escritura; corrupción del fichero → recreación.
- **GitHub**: contrato `IRemoteRepository` contra FakeGitHub por HTTP real; mapeo de cada código HTTP a `Error`; reintentos y no-reintento del PATCH; rate limit y `retry-after` (con `FakeTimeProvider`); ETag/304; paginación; truncated; zipball con zip slip; redacción del token en logs; device flow completo (pending, slow_down, denied, expired).
- **Secretos**: contrato en el SO correspondiente (`[Trait("OS","Windows")]`, `"macOS"`).

### 5.4 Presentación
- Cada ViewModel: estado inicial, comandos (CanExecute/Execute), reacción a eventos, manejo de errores (mensajes localizados mostrados vía `INotificationService` fake), cancelación.
- `EditorCommandsService`: tablas de `TextBufferState` antes/después para cada comportamiento de [07](07-interfaz-usuario.md) §5.2 (≥ 80 casos).
- `KeyBindingService`: resolución por plataforma, reasignación y conflictos.

### 5.5 UI headless
- Smoke de cada vista principal: se crea con su ViewModel, se renderiza sin excepciones, los bindings compilados funcionan.
- Flujos clave con entrada simulada: abrir quick switcher con el atajo y abrir nota; crear nota desde explorador; alternar modo lectura; completar `[[` en el editor; asistente de sync hasta dry-run (con FakeGitHub).
- Capturas de referencia (`PngSnapshot` sobre `CaptureRenderedFrame`) para tema claro/oscuro de 5 vistas clave; tolerancia de comparación configurada y línea base por plataforma (ADR-0009).

## 6. Property-based testing (obligatorio en estos puntos)

| Propiedad | Generadores |
|-----------|-------------|
| `VaultPath.Create(p.ToString()) == p` (round-trip) | `Arb.VaultPath` |
| `LineDiff.Apply(a, Diff(a,b)) == b` | líneas aleatorias con alfabeto pequeño (más colisiones) |
| Merge: identidades de [02](02-modelo-de-dominio.md) §10 | `Arb.TextEdit` sobre una base |
| Merge limpio contiene todas las inserciones de ambos lados | idem, ediciones no solapadas |
| `LinkRewriter`: texto fuera de spans intacto; idempotente | `Arb.MarkdownDocument` |
| `SyncPlanner` P1–P5 | `Arb.Manifest` triples (B, L, R) con SHAs de un universo pequeño |
| Índice SQLite ≡ índice en memoria | `Arb.Corpus`, `Arb.SearchQuery` |
| `MetadataCache` incremental ≡ reconstruida | `Arb.VaultOperation` secuencias |
| `IgnoreRules`: un patrón `!x` tras `x` des-ignora x | patrones generados |
| `GitTreeHasher` invariante al orden de inserción | manifiestos permutados |

Configuración: `MaxTest = 100` por defecto, `BASALT_PROPERTY_RUNS` en nightly = 10 000. Las semillas fallidas se imprimen y se añaden como casos de regresión `[Fact]`.

## 7. Simulación multi-dispositivo de sincronización (`Basalt.Sync.IntegrationTests`)

Es la red de seguridad del requisito "nunca perder datos".

### 7.1 Escenarios deterministas (catálogo mínimo)

Cada uno con 2 o 3 dispositivos (`SimDevice`: vault en directorio temporal real + `TestHost` completo con su propio `DeviceId`, `FakeTimeProvider` compartido) y un `FakeGitHubServer` real por HTTP:

| # | Escenario |
|---|-----------|
| S01 | Vault local → repo nuevo (Upload); otro dispositivo clona; contenidos idénticos byte a byte |
| S02 | Edición en A, sync A, sync B → B tiene la edición |
| S03 | Ediciones en A y B de **líneas distintas** de la misma nota → fusión limpia en ambos |
| S04 | Ediciones en A y B de la **misma línea** → conflicto: remoto en la ruta, copia de conflicto con la versión del segundo en sincronizar; ambos convergen |
| S05 | Igual que S04 con política `AutoMergeOrMarkers` |
| S06 | A borra, B modifica (`KeepModified`) → la nota sobrevive con la modificación |
| S07 | A borra, B modifica (`PreferDelete`) → borrada; versión en historial y `overwritten/` |
| S08 | A y B crean la misma ruta con contenidos distintos (fila 15) |
| S09 | A renombra, B edita la nota → sin pérdidas (§7.2) |
| S10 | Conflicto binario (imagen) |
| S11 | Carrera non-FF: hook `BeforeUpdateRef` publica desde B → A reintenta y converge |
| S12 | 6 carreras consecutivas → A agota reintentos con error transitorio, siguiente ciclo converge |
| S13 | Edición del usuario durante la fase de aplicación (hook en `LocalApplier`) → ruta aplazada, siguiente ciclo fusiona |
| S14 | Cancelación/crash en cada paso 1–10 (parametrizado) → reanudación converge sin pérdida |
| S15 | Corte de red en descarga de blobs, en create-tree, en PATCH ref |
| S16 | Commit externo desde "la web" (`server.CommitFiles`) mientras A tiene cambios |
| S17 | Force-push externo que elimina el historial (base sin blobs) |
| S18 | Rutas remotas inválidas para Windows y colisión de mayúsculas → cuarentena, sin borrados remotos |
| S19 | Fichero/carpeta con el mismo nombre |
| S20 | Ficheros > límite → no se suben, aviso |
| S21 | Repo vacío (409) → inicialización |
| S22 | Árbol truncado (umbral bajo en FakeGitHub) |
| S23 | Clonado con zipball, incluidos ficheros con `export-ignore` simulados |
| S24 | Token revocado (401) → `AuthRequired`, sin cambios locales, reanudación tras re-login |
| S25 | Rate limit primario y secundario → espera y continúa |
| S26 | Dos vaults en el mismo dispositivo contra dos repos de **cuentas distintas** sincronizando a la vez; error inyectado en uno no afecta al otro |
| S27 | Ignorados: `.basalt/workspace.*.json` nunca llega al remoto; `syncignore` compartido se respeta en el otro dispositivo |
| S28 | CRLF y BOM preservados byte a byte entre dispositivos |
| S29 | Nombres Unicode NFD creados en "macOS" → NFC en remoto → coinciden en Windows |
| S30 | Vinculación inicial modo Merge con ficheros iguales, distintos y exclusivos |
| S31 | Pérdida de `base.json` → modo fusión inicial, sin pérdida |
| S32 | Nota abierta en el editor con cambios sin guardar mientras la sync trae cambios remotos (a nivel de ViewModel con TestHost) |

### 7.2 Simulación aleatoria basada en modelo

`RandomizedSyncSimulation` (FsCheck o generador propio con semilla):

1. Crear N dispositivos (2–4) y un repo.
2. Generar una secuencia de 20–200 pasos aleatorios, cada uno: `Edit(device, path, edit)`, `Create`, `Delete`, `Rename`, `CreateFolder`, `WebCommit(path, content)`, `Sync(device)`, `SyncWithCrashAt(device, step)`, `NetworkFault(endpoint)`, `Race(deviceA, deviceB)`.
3. Mantener un **oráculo**: conjunto de todas las versiones de contenido "vivas" que el usuario ha escrito y no ha borrado explícitamente después.
4. Al final: sincronizar todos los dispositivos en ronda hasta estabilidad (máx. 3 rondas).
5. **Aserciones**:
   - **Convergencia**: todos los dispositivos y el remoto tienen manifiestos idénticos (excluyendo ignorados y cuarentena).
   - **Sin pérdida**: cada versión viva del oráculo existe como contenido de algún fichero (ruta original o copia de conflicto) o, solo si la política lo permite, en `overwritten/`/historial.
   - **Sin basura**: no hay ficheros temporales, staging vacío, journals limpios.
   - **Idempotencia**: una sync adicional en cualquier dispositivo no produce commit.
6. Al fallar: imprimir semilla y secuencia minimizada (shrinking) → añadir como escenario determinista.

Número de escenarios: `BASALT_SIM_SCENARIOS` (PR: 1 000 con dispositivos en memoria + 50 con FS real y HTTP; nightly: 10 000 + 500).

Para que 1 000 escenarios quepan en el tiempo de PR, la simulación usa `TestHost` con `InMemoryVaultFileSystem` + `InMemoryRemoteRepository` (ambos validados por los mismos contratos que los reales, por lo que la sustitución es legítima — principio de Liskov).

## 8. Tests de arquitectura

- Reglas R1–R7 de [01](01-arquitectura.md) §2.2.
- Independencia de módulos del dominio:

| Módulo | Puede depender de |
|--------|-------------------|
| Common | — |
| Notes | Common |
| Links | Common, Notes, Vaults (ajustes de formato de enlaces) |
| Tags | Common, Notes |
| Metadata | Common, Notes, Links, Tags (ADR-0010) |
| Search | Common, Notes, Tags, Metadata |
| Graph | Common, Notes, Links, Tags, Metadata |
| Templates | Common, Notes |
| Text | Common |
| Sync | Common, Notes, Text |
| Vaults, Accounts | Common |

- Convenciones: handlers `sealed`; todos los tipos de `Adapters.*` `internal` excepto extensiones de registro; ningún tipo de `Presentation` referencia `Avalonia.*`; todos los ViewModels heredan de `ViewModelBase`; ningún `async void` fuera de `Basalt.UI`.
- **DI**: el contenedor de cada head (con fakes de plataforma) se construye con `ValidateOnBuild`/`ValidateScopes` y resuelve todos los ViewModels y handlers registrados.
- Ningún puerto de salida sin implementación registrada; ningún puerto sin fake y sin contrato (reflexión sobre `Basalt.Application.Ports`).

## 9. Trazabilidad especificación ↔ tests

- Cada test que implementa un escenario o regla lleva `[Trait("Spec", "<id>")]` (p. ej. `F-004.4`, `SYNC-ROW-10`, `S14`, `NFR-030`).
- Identificadores de reglas de sync: `SYNC-ROW-1`…`SYNC-ROW-15`, `SYNC-P1`…`SYNC-P5`, `SYNC-7.1`…`SYNC-7.10`.
- `Basalt.Architecture.Tests/SpecTraceabilityTests` analiza `docs/specs/*.md` (regex de `F-\d{3}\.\d+`, `S\d{2}`, filas de la tabla de §5 de 06, `NFR-\d{3}` con verificación automatizable) y falla listando los identificadores sin ningún test con ese `Trait`. Los NFR marcados como "Manual" se excluyen.

## 10. Umbrales de calidad (puerta de CI)

| Proyecto | Cobertura de líneas | Cobertura de ramas | Mutación (Stryker) |
|----------|---------------------|--------------------|--------------------|
| `Basalt.Domain` | ≥ 95 % | ≥ 90 % | ≥ 80 % (break) / objetivo 90 % |
| `Basalt.Application` | ≥ 90 % | ≥ 85 % | ≥ 70 % (break) |
| `Basalt.Adapters.*` (excepto plataforma) | ≥ 80 % | ≥ 70 % | — |
| `Basalt.Presentation` | ≥ 85 % | ≥ 75 % | — |
| `Basalt.UI` | Sin umbral (smoke + flujos) | — | — |

Además: cero tests omitidos (`Skip`) sin issue enlazado en el motivo; cero tests inestables (un test que falla de forma intermitente se trata como bug prioritario); tiempo total de la suite de PR < 10 min.

## 11. Anti-patrones prohibidos

- Tests que solo verifican que se llamó a un mock con los mismos argumentos que la implementación (tautológicos).
- Tests sin aserciones o con `Assert.True(true)`.
- `Thread.Sleep`/esperas con tiempo real para sincronizar (usar `FakeTimeProvider`, `TaskCompletionSource`, o sondeo con timeout solo en integración de watcher).
- Dependencia del orden de ejecución o de estado global compartido entre tests.
- Acceso a red real fuera de `LiveTests`.
- Reducir umbrales de cobertura/mutación para que pase CI.
