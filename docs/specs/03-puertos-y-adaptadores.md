# 03 — Puertos y adaptadores

Este documento fija los **contratos** del núcleo. Las firmas son normativas (pueden añadirse sobrecargas o miembros auxiliares, pero no cambiar semántica). Todos los puertos viven en `Basalt.Application/Ports/...` (salida) o `Basalt.Application/<Módulo>/` (entrada).

Convenciones:
- Todos los métodos de E/S son `async` y aceptan `CancellationToken ct` como último parámetro.
- Los errores esperados se devuelven con `Result<T>`; los adaptadores no dejan escapar excepciones de E/S.
- Cada puerto de salida tiene: (1) un **fake en memoria** en `Basalt.Testing`, (2) un **adaptador real**, (3) una **clase base de tests de contrato** en `Basalt.Testing/Contracts/` que ambos superan.

## 1. Puertos de salida (driven)

### 1.1 Sistema de ficheros del vault

```csharp
public interface IVaultFileSystemFactory
{
    Task<Result<IVaultFileSystem>> OpenAsync(string rootLocation, CancellationToken ct);
}

// Segregado por capacidades; el adaptador real implementa todas.
public interface IFileReader
{
    Task<Result<byte[]>> ReadAllBytesAsync(VaultPath path, CancellationToken ct);
    Task<Result<string>> ReadAllTextAsync(VaultPath path, CancellationToken ct); // detecta BOM/UTF-8
    Task<Result<Stream>> OpenReadAsync(VaultPath path, CancellationToken ct);
    Task<Result<FileStat>> StatAsync(VaultPath path, CancellationToken ct);       // Size, LastWriteUtc, IsDirectory
    Task<bool> ExistsAsync(VaultPath path, CancellationToken ct);
}

public interface IFileWriter
{
    // Escritura ATÓMICA: fichero temporal en la misma carpeta + flush + rename/replace.
    // Si expectedSha != null y el contenido actual en disco no coincide → Error "fs.concurrent_modification".
    Task<Result<Unit>> WriteAllBytesAsync(VaultPath path, ReadOnlyMemory<byte> content, BlobSha? expectedSha, CancellationToken ct);
    Task<Result<Unit>> CreateDirectoryAsync(VaultPath path, CancellationToken ct);
    Task<Result<Unit>> MoveAsync(VaultPath from, VaultPath to, CancellationToken ct);   // falla si 'to' existe; soporta cambio solo de mayúsculas
    Task<Result<Unit>> DeleteAsync(VaultPath path, CancellationToken ct);               // borrado físico (lo usa la papelera y la sync)
    Task<Result<Unit>> DeleteDirectoryIfEmptyAsync(VaultPath path, CancellationToken ct);
}

public interface IFileEnumerator
{
    IAsyncEnumerable<FileEntry> EnumerateAsync(VaultPath root, bool recursive, CancellationToken ct); // incluye ocultos; el filtrado lo hace el núcleo
}

public interface IFileWatcher : IAsyncDisposable
{
    // Eventos agrupados (debounce 200 ms) y normalizados a VaultPath; tolera ráfagas y overflow (emite RescanRequired).
    IAsyncEnumerable<FileSystemChange> WatchAsync(CancellationToken ct);
}

public interface IVaultFileSystem : IFileReader, IFileWriter, IFileEnumerator
{
    string RootLocation { get; }
    bool IsCaseSensitive { get; }
    IFileWatcher CreateWatcher();
}
```

- Adaptador: `Basalt.Adapters.FileSystem` (`PhysicalVaultFileSystem`). En iOS, el root está en el contenedor de la app (`Documents/Vaults/<nombre>`); los ficheros se exponen en la app Archivos mediante `UIFileSharingEnabled` + `LSSupportsOpeningDocumentsInPlace` (ver [12](12-plataformas-build-ci.md)).
- Fake: `InMemoryVaultFileSystem` (con opción `caseSensitive` y con inyección de fallos: `FailNextWrite(path, error)`).
- Los ficheros temporales de escritura atómica usan el patrón `.~basalt-<guid>.tmp` y DEBEN ignorarse siempre en enumeración, watcher e indexación.

### 1.2 Análisis de Markdown

```csharp
public interface IMarkdownAnalyzer
{
    NoteMetadata Analyze(string content);                 // síncrono, puro respecto a su entrada, thread-safe
}
public interface IMarkdownRenderer
{
    RenderDocument Render(string content, RenderContext context); // modelo de render propio (no HTML), ver 07 §6
}
public interface IFrontmatterEditor
{
    string SetProperty(string content, string key, FrontmatterValue value);   // preserva el resto del YAML y del documento
    string RemoveProperty(string content, string key);
    Result<Frontmatter> Parse(string yaml);
}
```

Adaptador: `Basalt.Adapters.Markdown` (Markdig + extensiones propias para wikilinks, embeds, tags, block ids, highlights `==x==`, comentarios `%%x%%`, callouts `> [!note]`, YamlDotNet para frontmatter).

### 1.3 Índice y caché de metadatos

```csharp
public interface INoteIndexWriter
{
    Task UpsertAsync(IndexedNote note, CancellationToken ct);         // ruta, sha, mtime, texto, metadatos
    Task RemoveAsync(VaultPath path, CancellationToken ct);
    Task RenameAsync(VaultPath from, VaultPath to, CancellationToken ct);
    Task<IndexSnapshotInfo> GetSnapshotInfoAsync(CancellationToken ct); // para reindexado incremental al abrir
    Task ResetAsync(CancellationToken ct);
}
public enum SearchSort { Relevance, NameAscending, NameDescending, ModifiedNewest, ModifiedOldest }
public sealed record SearchOptions(SearchSort Sort = SearchSort.Relevance, bool MatchCase = false, int? Limit = null);
public sealed record SearchHit(VaultPath Path, FileKind Kind, DateTimeOffset LastWriteUtc, IReadOnlyList<MatchRange> Ranges, double Score);
public interface INoteIndexReader
{
    Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, SearchOptions options, CancellationToken ct);   // consulta vacía → []
    Task<IReadOnlyList<IndexedNoteSummary>> GetAllSummariesAsync(CancellationToken ct);
    Task<IndexedNote?> GetAsync(VaultPath path, CancellationToken ct);
}
```

Adaptador: `Basalt.Adapters.Index` (SQLite + FTS5) en `{AppData}/vaults/{vaultId}/index.db` (fuera del vault, no se sincroniza). Fake: `InMemoryNoteIndex` (búsqueda lineal usando `INoteMatcher` del dominio — sirve también de oráculo en tests de contrato).

La **caché de metadatos en memoria** (`IMetadataCache`) es un componente de **Aplicación** (no un puerto), construido sobre `INoteIndexReader` al abrir y actualizado por eventos. Implementa `IVaultFileCatalog` (dominio) y mantiene el `LinkGraph`.

### 1.4 Remoto Git (GitHub)

```csharp
public interface IRemoteRepositoryFactory
{
    IRemoteRepository Create(RemoteRepositoryRef repo, GitHubAccountId account);
}

public sealed record RemoteRepositoryRef(GitHubHost Host, string Owner, string Name, string Branch);

public interface IRemoteRepository
{
    Task<Result<RemoteHead?>> GetHeadAsync(CancellationToken ct);                          // null si la rama no existe / repo vacío
    Task<Result<Manifest>> GetTreeAsync(TreeSha tree, CancellationToken ct);               // recursivo; maneja 'truncated'
    Task<Result<byte[]>> GetBlobAsync(BlobSha sha, CancellationToken ct);
    Task<Result<BlobSha>> CreateBlobAsync(ReadOnlyMemory<byte> content, CancellationToken ct);
    Task<Result<TreeSha>> CreateTreeAsync(TreeSha? baseTree, IReadOnlyList<TreeChange> changes, CancellationToken ct); // TreeChange: Upsert(path, sha) | Delete(path)
    Task<Result<CommitSha>> CreateCommitAsync(string message, TreeSha tree, IReadOnlyList<CommitSha> parents, CommitAuthor author, CancellationToken ct);
    // Fast-forward únicamente. Si no es FF → Error "remote.non_fast_forward".
    Task<Result<Unit>> UpdateBranchAsync(CommitSha newHead, CommitSha? expectedOld, CancellationToken ct);
    Task<Result<IReadOnlyList<RemoteCommitInfo>>> GetFileHistoryAsync(VaultPath path, int max, CancellationToken ct);
    Task<Result<byte[]?>> GetFileAtCommitAsync(VaultPath path, CommitSha commit, CancellationToken ct);
}
public sealed record RemoteHead(CommitSha Commit, TreeSha Tree);

public interface IRepositoryAdministration
{
    Task<Result<IReadOnlyList<RemoteRepositoryInfo>>> ListAccessibleRepositoriesAsync(GitHubAccountId account, CancellationToken ct);
    Task<Result<RemoteRepositoryInfo>> CreateRepositoryAsync(GitHubAccountId account, string? org, string name, bool isPrivate, CancellationToken ct);
    Task<Result<RemoteRepositoryInfo>> GetRepositoryAsync(GitHubAccountId account, string owner, string name, CancellationToken ct);
}
```

Adaptador: `Basalt.Adapters.GitHub` (cliente REST propio sobre `HttpClient`, JSON con source generators). Fakes: `InMemoryRemoteRepository` (en `Basalt.Testing`, objetos Git reales con SHA correctos) y `FakeGitHubServer` (en `Basalt.Testing.FakeGitHub`, HTTP real). Detalle en [06](06-sincronizacion-github.md) §9.

### 1.5 Autenticación y secretos

```csharp
public interface IGitHubAuthenticator
{
    // OAuth Device Flow
    Task<Result<DeviceCodeChallenge>> StartDeviceFlowAsync(GitHubHost host, CancellationToken ct);        // user_code, verification_uri, interval, expires_in
    Task<Result<GitHubToken>> PollForTokenAsync(DeviceCodeChallenge challenge, CancellationToken ct);   // respeta interval y slow_down
    Task<Result<GitHubUserInfo>> GetUserAsync(GitHubHost host, GitHubToken token, CancellationToken ct);
    Task<Result<GitHubToken>> RefreshAsync(GitHubHost host, GitHubToken token, CancellationToken ct);    // si el token tiene refresh_token (GitHub App)
}

public interface ISecretStore
{
    Task<Result<Unit>> SetAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken ct);
    Task<Result<byte[]?>> GetAsync(string key, CancellationToken ct);
    Task<Result<Unit>> DeleteAsync(string key, CancellationToken ct);
}
```

- `ISecretStore` adaptadores: Windows → Credential Manager (`CredWrite/CredRead` P/Invoke) con fallback DPAPI (`ProtectedData`, ámbito usuario) en fichero; macOS/iOS → Keychain (`SecItemAdd/SecItemCopyMatching`, `kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly` para permitir sync en segundo plano en iOS). Fake: `InMemorySecretStore`.
- Clave del token: `basalt/github-token/{accountId}`.
- `IGitHubTokenProvider` (Aplicación): obtiene token por cuenta, refresca si caduca, invalida y emite `AccountReauthenticationRequired` ante 401.

### 1.6 Almacenes de configuración y estado

```csharp
public interface IVaultRegistry            // lista de vaults conocidos en este dispositivo
{
    Task<IReadOnlyList<VaultRegistration>> GetAllAsync(CancellationToken ct);
    Task<Result<Unit>> AddAsync(VaultRegistration reg, CancellationToken ct);
    Task<Result<Unit>> UpdateAsync(VaultRegistration reg, CancellationToken ct);
    Task<Result<Unit>> RemoveAsync(VaultId id, CancellationToken ct);   // no borra ficheros
}
public interface IAccountStore { /* CRUD de GitHubAccount (sin tokens) */ }
public interface ISyncProfileStore         // perfil de sync por vault (local al dispositivo)
{
    Task<SyncProfile?> GetAsync(VaultId id, CancellationToken ct);
    Task<Result<Unit>> SaveAsync(SyncProfile profile, CancellationToken ct);
    Task<Result<Unit>> DeleteAsync(VaultId id, CancellationToken ct);
}
public interface ISyncStateStore           // base de sincronización + journal + caché de hashes, por vault
{
    Task<SyncBase> LoadBaseAsync(CancellationToken ct);
    Task<Result<Unit>> SaveBaseAsync(SyncBase @base, CancellationToken ct);             // atómico
    Task<SyncJournal?> LoadJournalAsync(CancellationToken ct);
    Task<Result<Unit>> SaveJournalAsync(SyncJournal journal, CancellationToken ct);
    Task<Result<Unit>> ClearJournalAsync(CancellationToken ct);
    Task<HashCache> LoadHashCacheAsync(CancellationToken ct);                           // (path, size, mtime) → sha
    Task SaveHashCacheAsync(HashCache cache, CancellationToken ct);
}
public interface IAppSettingsStore { /* ajustes globales de la app: tema, idioma, atajos, último vault */ }
public interface IVaultConfigStore         // .basalt/vault.json, .basalt/bookmarks.json, .basalt/workspace.<deviceId>.json dentro del vault (Load/Save de cada uno)
public interface IRecentFilesStore         // {AppData}/vaults/{vaultId}/recent.json: LoadAsync(VaultId) / SaveAsync(VaultId, paths)
{ /* lectura/escritura tipada vía IVaultFileSystem */ }
```

Adaptadores: ficheros JSON en `{AppData}` (`Basalt.Adapters.Platform`), escritos atómicamente. `{AppData}` = `%LOCALAPPDATA%\Basalt` (Windows), `~/Library/Application Support/Basalt` (macOS), `Library/Application Support/Basalt` del contenedor (iOS).

### 1.7 Servicios de plataforma y utilidades

```csharp
public interface IClock { DateTimeOffset UtcNow { get; } TimeZoneInfo LocalZone { get; } }
public interface IIdGenerator { Guid NewId(); }
public interface IRandom { int Next(int maxExclusive); double NextDouble(); }
public interface IDeviceInfo { DeviceId Id { get; } string DisplayName { get; } PlatformKind Platform { get; } }
public interface IPlatformPaths { string AppDataDirectory { get; } string DefaultVaultsDirectory { get; } string LogsDirectory { get; } }
public interface IConnectivityMonitor { bool IsOnline { get; } event EventHandler<bool> Changed; }
public interface IBackgroundSyncScheduler { void Schedule(TimeSpan earliest); void Cancel(); }   // iOS: BGTaskScheduler; escritorio: no-op (timer en proceso)
public interface IExternalLauncher { Task OpenUriAsync(Uri uri, CancellationToken ct); Task RevealInFileManagerAsync(string location, CancellationToken ct); }
public interface IClipboardService { Task<ClipboardContent> GetAsync(CancellationToken ct); Task SetTextAsync(string text, CancellationToken ct); }
public interface ICultureProvider { CultureInfo Current { get; } }   // fechas de plantillas y notas diarias
public interface ISystemTrash { bool IsAvailable { get; } Task<Result<Unit>> MoveToTrashAsync(string rootLocation, VaultPath path, CancellationToken ct); }   // DeleteMode.SystemTrash; si no está disponible se usa .trash/
public interface IEventBus { ValueTask PublishAsync<T>(T evt, CancellationToken ct) where T : IApplicationEvent; IDisposable Subscribe<T>(Func<T, CancellationToken, ValueTask> handler) where T : IApplicationEvent; }
```

### 1.8 Puertos de presentación (definidos en `Basalt.Presentation`, implementados en `Basalt.UI`)

```csharp
public interface IUiDispatcher { bool CheckAccess(); Task InvokeAsync(Action a); Task<T> InvokeAsync<T>(Func<T> f); void Post(Action a); }
public interface IDialogService
{
    Task<bool> ConfirmAsync(string titleKey, string messageKey, object?[] args, bool destructive);
    Task<string?> PromptAsync(string titleKey, string labelKey, string? initial, Func<string, string?>? validate);
    Task<string?> PickFolderAsync(string titleKey);
    Task<TResult?> ShowAsync<TViewModel, TResult>(TViewModel vm) where TViewModel : IDialogViewModel<TResult>;
}
public interface INotificationService { void Show(Notification n); }   // toasts in-app
public interface ILocalizer { string this[string key] { get; } string Format(string key, params object?[] args); }
```

## 2. Puertos de entrada (casos de uso)

Cada caso de uso es un `record` comando/consulta + un handler. Lista completa (los ViewModels solo usan estos):

### Vaults
| Caso de uso | Tipo | Resultado |
|-------------|------|-----------|
| `CreateVault(Name, ParentLocation)` | Command | `VaultRegistration` — crea carpeta, `.basalt/vault.json` con nuevo `VaultId` |
| `OpenVault(VaultId)` | Command | `VaultSessionInfo` — abre scope, carga/actualiza índice incremental |
| `OpenFolderAsVault(Location)` | Command | `VaultRegistration` — si no tiene `.basalt/vault.json`, lo crea |
| `CloseVault(VaultId)` | Command | `Unit` |
| `RemoveVaultFromList(VaultId)` | Command | `Unit` |
| `ListVaults()` | Query | `VaultRegistration[]` con estado de sync |
| `UpdateVaultSettings(VaultId, VaultSettings)` | Command | `Unit` |

### Ficheros y notas
| Caso de uso | Notas |
|-------------|-------|
| `CreateNote(VaultId, Folder?, Title?, TemplatePath?)` | Si no hay título → "Untitled", "Untitled 1"… en la carpeta configurada |
| `ReadNote(VaultId, Path)` | Query → `NoteDocument` (contenido + metadatos + sha) |
| `SaveNote(VaultId, Path, Content, ExpectedSha)` | Concurrencia optimista: si `ExpectedSha` no coincide → `note.modified_externally` con contenido actual para que la UI ofrezca recargar o fusionar |
| `RenameEntry(VaultId, Path, NewName)` | Nota, adjunto o carpeta; actualiza enlaces si está activado |
| `MoveEntries(VaultId, Paths[], TargetFolder)` | Idem |
| `DeleteEntries(VaultId, Paths[], Mode)` | `Mode`: `Trash` (mueve a `.trash/` — por defecto), `SystemTrash` (escritorio), `Permanent` |
| `CreateFolder(VaultId, Path)` | |
| `GetFileTree(VaultId)` | Query; árbol con orden configurable (nombre, fecha mod., fecha creación; asc/desc), carpetas primero |
| `ImportAttachment(VaultId, SourceNote, FileName, Bytes)` | Guarda en la carpeta de adjuntos según ajustes, devuelve enlace a insertar |
| `SetFrontmatterProperty(VaultId, Path, Key, Value)` / `RemoveFrontmatterProperty` | |
| `ToggleTask(VaultId, Path, Line)` | |

### Navegación y conocimiento
`GetBacklinks(VaultId, Path)` (enlazadas + menciones no enlazadas con contexto), `GetOutgoingLinks`, `LinkUnlinkedMention(VaultId, SourcePath, Span, TargetPath)`, `GetTags(VaultId)`, `Search(VaultId, QueryText, Options)`, `QuickSwitch(VaultId, Text)` (fuzzy sobre títulos + alias, incluye "crear nota X"), `GetOutline(VaultId, Path)`, `GetGraph(VaultId, GraphFilter)`, `GetLocalGraph(VaultId, Path, Depth, Filter)`, `ResolveLink(VaultId, SourcePath, LinkText)`, `SuggestLinkTargets(VaultId, SourcePath, Partial)` (autocompletado: notas, encabezados tras `#`, bloques tras `^`), `SuggestTags(VaultId, Partial)`.

### Productividad
`OpenDailyNote(VaultId, Date?)` (crea si no existe desde plantilla; hoy en la zona local por defecto; plantilla configurada inexistente → `entry.not_found` sin crear nada), `InsertTemplate(VaultId, TemplatePath, TargetPath, CurrentText, Cursor)` → `TemplateInsertion(Text, Cursor)`: devuelve el texto completo del editor con el cuerpo insertado en el cursor y las propiedades de la plantilla añadidas al frontmatter (prevalecen las de la nota); **no escribe en disco** (el editor aplica el resultado a su búfer y guarda como siempre, sin pisar ediciones sin guardar), `ListTemplates`, `AddBookmark/RemoveBookmark/ListBookmarks` (los marcadores siguen renombrados y movimientos; los de ficheros borrados se conservan), `RecordRecentFile/ListRecentFiles` (50 por vault y dispositivo; siguen renombrados y quitan borrados), `GetOutline`, `SaveWorkspace/LoadWorkspace(VaultId)` (pestañas, paneles, nota activa, por dispositivo), `ExecuteCommand(CommandId)` vía `ICommandRegistry`.

### Vaults (añadido)
`RelocateVault(VaultId, NewLocation)` (F-001.6 "Localizar…"): apunta el registro a la carpeta movida; una carpeta cuyo `vault.json` es de otro vault → `vault.id_mismatch`; sin `vault.json` se acepta (al abrir se recrea con esta identidad).

### Cuentas y sincronización
| Caso de uso | Notas |
|-------------|-------|
| `StartGitHubSignIn(Host)` | Devuelve `DeviceCodeChallenge` para mostrar código |
| `CompleteGitHubSignIn(Challenge)` | Sondea hasta token; guarda cuenta + token |
| `ListAccounts()` / `RemoveAccount(AccountId)` | Al eliminar una cuenta, los perfiles que la usan pasan a estado `NeedsAccount` |
| `ListRepositories(AccountId)` | |
| `ConfigureSync(VaultId, SyncProfileDraft)` | Valida (repo accesible, permisos de escritura, rama) y decide modo de enlace inicial |
| `CreateRepositoryForVault(VaultId, AccountId, Owner?, Name, Private)` | |
| `CloneVaultFromRepository(AccountId, RepoRef, LocalParent, VaultName)` | Crea vault local + perfil + primera sincronización (solo descarga) |
| `SyncVault(VaultId, Trigger)` | Ejecuta un ciclo de sincronización; devuelve `SyncOutcome` |
| `PauseSync(VaultId)` / `ResumeSync(VaultId)` / `DisableSync(VaultId)` | `DisableSync` borra perfil y estado, no toca ficheros ni el repo |
| `GetSyncStatus(VaultId)` | Query; estado actual + último resultado + cambios pendientes |
| `GetSyncLog(VaultId, Max)` | |
| `ListConflicts(VaultId)` | Copias de conflicto y ficheros con marcadores pendientes |
| `ResolveConflict(VaultId, ConflictId, Resolution)` | `KeepLocal`, `KeepRemote`, `KeepBoth`, `Merged(text)` |
| `GetFileHistory(VaultId, Path)` / `GetFileVersion(VaultId, Path, Commit)` / `RestoreFileVersion(...)` | Historial vía API de GitHub |

## 3. Matriz de adaptadores

| Puerto | Real | Fake (Basalt.Testing) | Tests de contrato |
|--------|------|------------------------|-------------------|
| `IVaultFileSystem` | `PhysicalVaultFileSystem` | `InMemoryVaultFileSystem` | `VaultFileSystemContract` |
| `IFileWatcher` | `PhysicalFileWatcher` (FileSystemWatcher + rescan) | `ManualFileWatcher` | `FileWatcherContract` (eventos esperados tras operaciones) |
| `IMarkdownAnalyzer` | `MarkdigAnalyzer` | — (se usa el real en tests de Aplicación; es puro y rápido) | snapshot tests |
| `INoteIndexReader/Writer` | `SqliteNoteIndex` | `InMemoryNoteIndex` | `NoteIndexContract` |
| `IRemoteRepository` | `GitHubRemoteRepository` | `InMemoryRemoteRepository` | `RemoteRepositoryContract` (contra in-memory, contra FakeGitHubServer vía HTTP, y Live opcional) |
| `IGitHubAuthenticator` | `GitHubDeviceFlowAuthenticator` | `FakeAuthenticator` | `AuthenticatorContract` contra FakeGitHubServer |
| `ISecretStore` | `WindowsCredentialStore`, `AppleKeychainStore` | `InMemorySecretStore` | `SecretStoreContract` (los reales se ejecutan solo en su SO, `[Trait("OS", ...)]`) |
| `I*Store` (JSON) | `JsonFileStore<T>` | `InMemoryStore<T>` | `KeyValueStoreContract` |
| `IClock` | `SystemClock` | `FakeClock` (avanzable) | — |
| `ICultureProvider` | `SystemCultureProvider` | `FakeCultureProvider` (es-ES) | — |
| `ISystemTrash` | `SystemTrash` (Windows: papelera de reciclaje; otros: no disponible → `fs.trash_unavailable`) | `FakeSystemTrash` | `SystemTrashContract` |
| `IUiDispatcher` | `AvaloniaUiDispatcher` | `ImmediateDispatcher` | — |
