# 01 — Arquitectura

## 1. Estilo: monolito hexagonal

Basalt es un **monolito modular con arquitectura hexagonal (Ports & Adapters)**:

- **Un único proceso** por plataforma; un único despliegue. No hay servicios remotos propios.
- **Núcleo** (Dominio + Aplicación) sin dependencias de E/S, de UI ni de plataforma.
- **Puertos** (interfaces) definidos por el núcleo:
  - *Puertos de entrada (driving)*: casos de uso que la UI invoca.
  - *Puertos de salida (driven)*: capacidades que el núcleo necesita (ficheros, red, índice, secretos, reloj…).
- **Adaptadores** en proyectos separados que implementan los puertos de salida o invocan los de entrada.
- **Raíz de composición** única por cabecera (head) de plataforma, que conecta adaptadores y núcleo vía DI.

```
                 ┌─────────────────────────── Adaptadores de entrada ───────────────────────────┐
                 │  Basalt.UI (Avalonia Views)  →  Basalt.Presentation (ViewModels, sin Avalonia) │
                 └───────────────────────────────────────┬───────────────────────────────────────┘
                                                         │ invoca puertos de entrada (casos de uso)
                 ┌───────────────────────────────────────▼───────────────────────────────────────┐
                 │                         Basalt.Application                                     │
                 │   Casos de uso (handlers) · Puertos de entrada · Puertos de salida · DTOs       │
                 │                 ┌─────────────────────────────────────┐                         │
                 │                 │            Basalt.Domain            │                         │
                 │                 │ Entidades · Value objects · Servicios│                         │
                 │                 │ de dominio puros (enlaces, búsqueda, │                         │
                 │                 │ diff/merge, planificador de sync)    │                         │
                 │                 └─────────────────────────────────────┘                         │
                 └───────────────────────────────────────┬───────────────────────────────────────┘
                                                         │ implementan puertos de salida
   ┌──────────────────┬──────────────────┬───────────────┴──┬───────────────────┬──────────────────┐
   │ Adapters.FileSys │ Adapters.GitHub  │ Adapters.Index   │ Adapters.Markdown │ Adapters.Platform │
   │ (System.IO,      │ (HttpClient,     │ (SQLite + FTS5)  │ (Markdig)         │ (.Windows/.MacOS/ │
   │  watcher)        │  OAuth device)   │                  │                   │  .iOS: secretos,  │
   │                  │                  │                  │                   │  rutas, bg tasks) │
   └──────────────────┴──────────────────┴──────────────────┴───────────────────┴──────────────────┘
```

## 2. Estructura de la solución

Fichero de solución: `Basalt.slnx` (formato SLNX de .NET 10).

```
/
├─ CLAUDE.md
├─ PROGRESS.md
├─ Basalt.slnx
├─ Directory.Build.props          # TFM, LangVersion, Nullable, TreatWarningsAsErrors, analizadores
├─ Directory.Packages.props       # Central Package Management (todas las versiones aquí)
├─ global.json                    # SDK .NET 10 fijado
├─ .editorconfig
├─ docs/specs/                    # estas especificaciones
├─ src/
│  ├─ Basalt.Domain/
│  ├─ Basalt.Application/
│  ├─ Basalt.Adapters.FileSystem/
│  ├─ Basalt.Adapters.Markdown/
│  ├─ Basalt.Adapters.Index/
│  ├─ Basalt.Adapters.GitHub/
│  ├─ Basalt.Adapters.Platform/          # implementaciones comunes + abstracciones de plataforma
│  ├─ Basalt.Adapters.Platform.Windows/  # net10.0-windows (DPAPI/Credential Manager)
│  ├─ Basalt.Adapters.Platform.Apple/    # Keychain para macOS e iOS (P/Invoke a Security.framework)
│  ├─ Basalt.Presentation/               # ViewModels (CommunityToolkit.Mvvm), sin referencia a Avalonia
│  ├─ Basalt.UI/                         # Vistas Avalonia, controles, temas, converters
│  ├─ Basalt.Composition/                # extensiones IServiceCollection por módulo (sin plataforma)
│  ├─ Basalt.Desktop/                    # head Windows + macOS (net10.0 / net10.0-windows)
│  └─ Basalt.iOS/                        # head iOS (net10.0-ios)
└─ tests/
   ├─ Basalt.Domain.Tests/
   ├─ Basalt.Application.Tests/
   ├─ Basalt.Adapters.FileSystem.Tests/      # integración con disco real (directorios temporales)
   ├─ Basalt.Adapters.Markdown.Tests/
   ├─ Basalt.Adapters.Index.Tests/           # integración con SQLite real
   ├─ Basalt.Adapters.GitHub.Tests/          # integración contra FakeGitHub (HTTP real en loopback)
   ├─ Basalt.Adapters.GitHub.LiveTests/      # opcional, contra github.com (gated por variable de entorno)
   ├─ Basalt.Presentation.Tests/             # ViewModels
   ├─ Basalt.UI.Tests/                       # Avalonia.Headless
   ├─ Basalt.Sync.IntegrationTests/          # extremo a extremo multi-dispositivo (FS real + FakeGitHub)
   ├─ Basalt.Architecture.Tests/             # reglas de dependencia (ArchUnitNET o NetArchTest)
   └─ Basalt.Testing/                        # utilidades compartidas: builders, fakes, contract tests base
      └─ Basalt.Testing.FakeGitHub/          # servidor ASP.NET Core en memoria que emula la API Git Data
```

### 2.1 Responsabilidad de cada proyecto

| Proyecto | Responsabilidad | Puede referenciar |
|----------|-----------------|-------------------|
| `Basalt.Domain` | Modelo de dominio y algoritmos puros | Solo BCL |
| `Basalt.Application` | Casos de uso, puertos, orquestación, DTOs, eventos de aplicación | `Domain`, `Microsoft.Extensions.Logging.Abstractions` |
| `Basalt.Adapters.*` | Implementan puertos de salida | `Application`, `Domain`, librerías externas propias del adaptador |
| `Basalt.Presentation` | ViewModels, navegación lógica, comandos UI | `Application`, `Domain`, `CommunityToolkit.Mvvm` |
| `Basalt.UI` | Vistas XAML, controles personalizados (editor, grafo), estilos | `Presentation`, Avalonia, AvaloniaEdit |
| `Basalt.Composition` | Registro DI de núcleo y adaptadores portables | todo salvo heads y adaptadores de plataforma |
| `Basalt.Desktop` / `Basalt.iOS` | Punto de entrada, registro de adaptadores de plataforma, ciclo de vida | todo |

### 2.2 Reglas de dependencia (verificadas por `Basalt.Architecture.Tests`)

- R1. `Basalt.Domain` NO DEBE referenciar ningún otro ensamblado de la solución ni paquetes NuGet (salvo analizadores).
- R2. `Basalt.Application` NO DEBE referenciar `Adapters.*`, `Presentation`, `UI`, Avalonia, `System.Net.Http`, `Microsoft.Data.Sqlite`, Markdig.
- R3. `Basalt.Domain` y `Basalt.Application` NO DEBEN usar `System.IO.File`, `System.IO.Directory`, `FileStream`, `DateTime.Now/UtcNow`, `DateTimeOffset.Now/UtcNow`, `Guid.NewGuid`, `Environment`, `Random` (se usan puertos `IClock`, `IIdGenerator`, `IRandom`). Verificado con analizador `BannedApiAnalyzers` (`BannedSymbols.txt`) además de los tests de arquitectura.
- R4. `Basalt.Presentation` NO DEBE referenciar Avalonia. La interacción con el hilo de UI se hace vía `IUiDispatcher`; los diálogos vía `IDialogService`.
- R5. Un adaptador NO DEBE referenciar otro adaptador.
- R6. Los tipos de los adaptadores DEBEN ser `internal` salvo la clase de extensión de registro DI (`public static class XxxServiceCollectionExtensions`). Esto fuerza el uso a través de puertos. Se usa `InternalsVisibleTo` para su proyecto de tests.
- R7. Ningún puerto de salida expone tipos de librerías externas (ni `HttpResponseMessage`, ni `SqliteConnection`, ni `MarkdownDocument` de Markdig).

## 3. Aplicación de SOLID

| Principio | Aplicación concreta |
|-----------|---------------------|
| **S** — Responsabilidad única | Un handler por caso de uso (`RenameNoteHandler`, `SyncVaultHandler`…). Los servicios de dominio hacen una cosa (`LinkResolver`, `ThreeWayTextMerger`, `SyncPlanner`, `GitBlobHasher`). ViewModels por panel, no "God ViewModel". |
| **O** — Abierto/cerrado | Nuevos comandos se añaden registrando un `ICommandContribution`; nuevas estrategias de conflicto implementando `IConflictResolutionStrategy`; nuevos tipos de vista con `IPaneFactory`. Sin `switch` sobre tipos en el núcleo para estas extensiones. |
| **L** — Sustitución de Liskov | Cada puerto de salida tiene **tests de contrato** (clase base abstracta) que se ejecutan contra el fake en memoria y contra el adaptador real; ambos DEBEN pasar la misma batería. |
| **I** — Segregación de interfaces | Puertos pequeños y específicos: `IFileReader`, `IFileWriter`, `IFileEnumerator`, `IFileWatcher` en lugar de un único `IFileSystem` gigante (un adaptador puede implementar varias). `INoteIndexWriter` separado de `INoteIndexReader`. |
| **D** — Inversión de dependencias | El núcleo define las interfaces; los adaptadores dependen del núcleo. Inyección por constructor exclusivamente; prohibido Service Locator fuera de la raíz de composición. |

## 4. Patrones transversales

### 4.1 Casos de uso (puertos de entrada)

Sin MediatR. Se define un patrón mínimo propio:

```csharp
public interface ICommandHandler<in TCommand, TResult> where TCommand : ICommand<TResult>
{
    Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken ct);
}

public interface IQueryHandler<in TQuery, TResult> where TQuery : IQuery<TResult>
{
    Task<Result<TResult>> HandleAsync(TQuery query, CancellationToken ct);
}
```

- Los comandos/queries son `record` inmutables.
- Los ViewModels inyectan directamente el handler concreto por su interfaz (`ICommandHandler<RenameNote, NoteRenamed>`), lo que hace explícitas sus dependencias.
- Decoradores transversales (registrados en DI con `Scrutor` o a mano): `LoggingHandlerDecorator`, `ValidationHandlerDecorator`, `VaultLockDecorator` (serializa escrituras por vault).

### 4.2 Gestión de errores: `Result<T>`

- Los errores **esperados** (nota no existe, nombre inválido, conflicto, sin red, token caducado) se devuelven como `Result<T>.Failure(Error)` — no excepciones.
- `Error` es un `record` con `Code` (string estable, p. ej. `"note.not_found"`), `Message` (inglés, para logs) y metadatos. La UI traduce `Code` → texto localizado.
- Las excepciones se reservan para errores de programación o fallos inesperados; los adaptadores DEBEN capturar excepciones de E/S/red y traducirlas a `Error` en la frontera del puerto.
- Catálogo de códigos de error en `Basalt.Application/Errors/ErrorCodes.cs` (ver [13](13-convenciones-de-codigo.md)).

### 4.3 Eventos

- **Eventos de dominio** (`NoteRenamed`, `NoteDeleted`…): devueltos por las operaciones de dominio y publicados por el handler tras persistir.
- **Bus de eventos de aplicación** in-process: `IEventBus` (puerto) con implementación `InMemoryEventBus` en Aplicación (basada en canales; entrega asíncrona en orden por vault). Suscriptores: actualizador de índice, ViewModels (vía `Presentation`), disparador de sincronización.
- Eventos externos del sistema de ficheros (cambios hechos por otras apps) llegan por `IFileWatcher` y se traducen a eventos de aplicación (`VaultFileChangedExternally`).

### 4.4 Concurrencia

- Toda E/S es `async` con `CancellationToken`.
- **Por vault**: un `VaultSession` mantiene un `AsyncReaderWriterLock` lógico: lecturas concurrentes, escrituras de contenido serializadas; la sincronización toma el bloqueo de escritura solo durante la fase de aplicación local (ver [06](06-sincronizacion-github.md)).
- **Entre vaults**: totalmente independientes; cada vault abierto tiene su propia `VaultSession` (scope DI).
- ViewModels: las actualizaciones de propiedades enlazadas se hacen en el hilo de UI vía `IUiDispatcher`.

### 4.5 Ámbitos de inyección de dependencias

| Ámbito | Ejemplos |
|--------|----------|
| Singleton (aplicación) | `IClock`, `IVaultRegistry`, `IAccountStore`, `ISecretStore`, `IGitHubAuthenticator`, `HttpClient` factory, `IEventBus` raíz, `SyncCoordinator` |
| Scope por vault (`IVaultScope`) | `VaultSession`, `IVaultFileSystem` enraizado, `INoteIndex`, `IMetadataCache`, `ISyncEngine` del vault, `ISyncStateStore` del vault |
| Transient | Handlers, ViewModels de diálogos |

`IVaultScopeFactory.OpenAsync(VaultId)` crea el scope; cerrar el vault lo dispone.

## 5. Módulos funcionales dentro del núcleo

Para mantener el monolito modular, Dominio y Aplicación se organizan por carpetas/namespaces de módulo (no por tipo técnico):

```
Basalt.Domain/
  Common/        (Result, Error, Guard, value object base)
  Vaults/        (Vault, VaultId, VaultSettings)
  Notes/         (Note, NotePath, NoteName, Frontmatter, Heading, BlockId)
  Links/         (Link, LinkTarget, LinkResolver, LinkRewriter)
  Tags/          (Tag, TagHierarchy)
  Search/        (SearchQuery, SearchQueryParser, Matchers)
  Graph/         (GraphModel, GraphBuilder)
  Templates/     (Template, TemplateRenderer)
  Sync/          (Manifest, BlobSha, SyncPlan, SyncPlanner, ConflictCopyNamer, IgnoreRules)
  Text/          (LineDiff (Myers), ThreeWayTextMerger, TextEncodingDetector)
Basalt.Application/
  Vaults/ Notes/ Links/ Search/ Graph/ Templates/ DailyNotes/ Bookmarks/ Workspace/ Sync/ Accounts/ Settings/ Commands/
  Ports/   (Driven: interfaces de salida agrupadas por módulo)
```

Los módulos se comunican solo vía interfaces públicas o eventos; los tests de arquitectura verifican que `Domain.Sync` no depende de `Domain.Graph`, etc. (ver matriz en [09](09-estrategia-de-pruebas.md) §8).

## 6. Tecnologías y paquetes

Todas las versiones se fijan en `Directory.Packages.props`. Se usa la **última versión estable** disponible al inicio de la Fase 0, que el agente DEBE registrar en `docs/specs/adr/0001-stack.md`.

| Uso | Paquete | Notas |
|-----|---------|-------|
| UI | `Avalonia`, `Avalonia.Desktop`, `Avalonia.iOS`, `Avalonia.Themes.Fluent`, `Avalonia.Fonts.Inter` | 11.3.x o superior estable compatible con `net10.0-ios` |
| Editor | `Avalonia.AvaloniaEdit`, `AvaloniaEdit.TextMate` | Resaltado Markdown con gramática TextMate |
| MVVM | `CommunityToolkit.Mvvm` | Generadores de código `[ObservableProperty]`, `[RelayCommand]` |
| DI | `Microsoft.Extensions.DependencyInjection`, `.Hosting` (solo heads) | |
| Logging | `Microsoft.Extensions.Logging` + `Serilog.Extensions.Logging` + `Serilog.Sinks.File` | Logs rotativos en carpeta de datos de app |
| Markdown | `Markdig` | Solo en `Adapters.Markdown` |
| YAML | `YamlDotNet` | Solo en `Adapters.Markdown` (frontmatter) |
| Índice | `Microsoft.Data.Sqlite` + `SQLitePCLRaw.bundle_e_sqlite3` | FTS5 incluido en e_sqlite3 |
| JSON | `System.Text.Json` con **source generators** (`JsonSerializerContext`) | Obligatorio por trimming/AOT en iOS |
| HTTP | `System.Net.Http` + `Microsoft.Extensions.Http` + `Microsoft.Extensions.Http.Resilience` | Cliente GitHub propio (no Octokit, ver ADR-0003) |
| Tests | `xunit.v3`, `AwesomeAssertions`, `NSubstitute`, `FsCheck.Xunit.v3`, helper de snapshots propio (ADR-0009), `Bogus`, `coverlet.MTP` / `Microsoft.Testing.Extensions.CodeCoverage`, `Avalonia.Headless.XUnit`, `ArchUnitNET.xUnitV3` (o `NetArchTest.eNhancedEdition`), `Microsoft.AspNetCore.TestHost`/Kestrel para FakeGitHub | Ver [09](09-estrategia-de-pruebas.md) |
| Mutación | `dotnet-stryker` (herramienta local en `.config/dotnet-tools.json`) | |

Si una combinación de versiones resulta incompatible (por ejemplo, Avalonia.Headless con xUnit v3), el agente DEBE documentar la alternativa elegida en un ADR (p. ej. usar `Avalonia.Headless.NUnit` solo en `Basalt.UI.Tests`).

## 7. Raíz de composición

```csharp
// Basalt.Composition
public static class BasaltCoreRegistration
{
    public static IServiceCollection AddBasaltCore(this IServiceCollection s) => s
        .AddBasaltDomainServices()        // LinkResolver, SyncPlanner, ThreeWayTextMerger...
        .AddBasaltApplication()           // handlers + decoradores + event bus
        .AddBasaltFileSystemAdapter()
        .AddBasaltMarkdownAdapter()
        .AddBasaltIndexAdapter()
        .AddBasaltGitHubAdapter()
        .AddBasaltPresentation();
}

// Basalt.Desktop/Program.cs
services.AddBasaltCore()
        .AddBasaltWindowsPlatform()   // o AddBasaltMacOSPlatform() según OperatingSystem.IsWindows()/IsMacOS()
        .AddBasaltUI();
```

- `Basalt.Architecture.Tests` incluye un test que construye el `ServiceProvider` con `ValidateOnBuild = true` y `ValidateScopes = true` para cada head (con adaptadores de plataforma sustituidos por fakes) y resuelve todos los ViewModels raíz.
