# 02 — Modelo de dominio

Todo lo descrito aquí vive en `Basalt.Domain`, es **puro** (sin E/S, sin reloj, sin aleatoriedad) y DEBE tener cobertura de líneas ≥ 95 % y de ramas ≥ 90 %.

## 1. Primitivas comunes (`Domain.Common`)

### 1.1 `Result` / `Result<T>` / `Error`

```csharp
public sealed record Error(string Code, string Message, IReadOnlyDictionary<string, object?>? Metadata = null);

public readonly record struct Result<T>
{
    public bool IsSuccess { get; }
    public T Value { get; }          // lanza InvalidOperationException si !IsSuccess
    public Error Error { get; }      // lanza InvalidOperationException si IsSuccess
    public static Result<T> Success(T value);
    public static Result<T> Failure(Error error);
    public Result<TOut> Map<TOut>(Func<T, TOut> f);
    public Result<TOut> Bind<TOut>(Func<T, Result<TOut>> f);
    public Task<Result<TOut>> BindAsync<TOut>(Func<T, Task<Result<TOut>>> f);
}
public readonly record struct Unit;  // para Result<Unit>
```

### 1.2 Value objects

- Todos los value objects son `readonly record struct` o `sealed record`, inmutables, con constructor privado y método `static Result<X> Create(...)` que valida. Para usos internos con datos ya validados se permite `X.FromTrusted(...)` marcado `internal`.
- Igualdad por valor; `ToString()` devuelve la representación canónica.

## 2. Vaults (`Domain.Vaults`)

| Tipo | Descripción e invariantes |
|------|---------------------------|
| `VaultId` | `Guid` envuelto. Se genera al crear el vault (vía `IIdGenerator`) y se persiste en `.basalt/vault.json`. Estable entre dispositivos (viaja en el repo). |
| `VaultName` | 1–100 caracteres, sin `/ \ : * ? " < > \|`, sin espacios al inicio/fin. |
| `Vault` | Agregado: `Id`, `Name`, `RootLocation` (opaco, string de la plataforma; el dominio no lo interpreta), `Settings`. |
| `VaultSettings` | Ajustes compartidos del vault (sincronizados): carpeta de adjuntos por defecto, carpeta de notas nuevas, formato de enlaces (`Wikilink` \| `Markdown`), uso de rutas en enlaces (`Shortest` \| `Relative` \| `Absolute`), actualización automática de enlaces (bool), notas diarias (carpeta, formato de fecha, plantilla), carpeta de plantillas, extensiones de ficheros visibles. |

## 3. Rutas (`Domain.Notes`)

### 3.1 `VaultPath`

Ruta **relativa a la raíz del vault**, canónica:

- Separador `/` siempre (también en Windows).
- Normalizada a **Unicode NFC** (macOS devuelve NFD en algunos casos; la canónica es NFC).
- Sin `.` ni `..` resueltos tras normalizar; si al normalizar sale de la raíz → error `path.outside_vault`.
- Sin `/` inicial ni final; sin segmentos vacíos (`a//b` → `a/b`).
- Cada segmento: 1–255 bytes UTF-8, sin caracteres prohibidos en Windows (`< > : " \ | ? *`, controles 0–31), sin terminar en espacio o punto, sin nombres reservados de Windows (`CON`, `PRN`, `AUX`, `NUL`, `COM1-9`, `LPT1-9`, sin importar extensión). Esto garantiza que un vault sincronizado desde macOS/iOS pueda materializarse en Windows. Error: `path.invalid_segment` con el segmento.
- Raíz representada por `VaultPath.Root` (cadena vacía).
- Propiedades: `Segments`, `Name` (último segmento), `Extension` (minúsculas, sin punto), `NameWithoutExtension`, `Parent`, `IsRoot`, `IsHidden` (algún segmento empieza por `.`).
- Operaciones: `Combine(string)`, `WithName(string)`, `ChangeExtension(string)`, `IsDescendantOf(VaultPath)`, `RelativeTo(VaultPath)`.
- `CaseFoldKey`: clave para comparación insensible a mayúsculas (`ToUpperInvariant` tras NFC) — usada para detectar colisiones de mayúsculas en FS insensibles (Windows/macOS).

### 3.2 Clasificación de ficheros

`FileKind Classify(VaultPath)`: `Note` (`.md`), `Image` (png, jpg, jpeg, gif, webp, svg, bmp), `Pdf`, `Audio`, `Video`, `Other`. Rutas bajo `.basalt/` o `.trash/` → `Internal`/`Trash`.

## 4. Notas (`Domain.Notes`)

| Tipo | Descripción |
|------|-------------|
| `NoteTitle` | Nombre visible = `NameWithoutExtension` de la ruta. |
| `Note` | `Path: VaultPath`, `Content: NoteContent`, `Metadata: NoteMetadata`. |
| `NoteContent` | Texto completo (string) + `LineEnding` detectado (`LF`/`CRLF`/`Mixed`) + `HasBom`. Al guardar se preserva el final de línea original y la BOM. |
| `NoteMetadata` | (Módulo `Domain.Metadata`, ver ADR-0010.) Resultado del análisis: `Frontmatter`, `Headings[]`, `Links[]`, `Embeds[]`, `Tags[]`, `Blocks[]`, `Tasks[]`, `WordCount`, `Aliases[]`. Es un **valor** recalculable a partir de `NoteContent` mediante el puerto `IMarkdownAnalyzer`. |
| `Frontmatter` | Mapa ordenado `string → FrontmatterValue` (string, number, bool, date, list, null) + rango de texto original (`SourceSpan`) para poder reescribirlo preservando formato. Claves especiales: `aliases`/`alias`, `tags`/`tag`, `cssclasses` (se conserva, no se usa). |
| `Heading` | `Level` 1–6, `Text`, `Line`, `Slug` (para anclas, ver [05](05-markdown-y-enlaces.md)). |
| `BlockId` | `^[A-Za-z0-9-]+` al final de un párrafo/línea. |
| `TaskItem` | `- [ ]`/`- [x]` con línea y estado. |
| `SourceSpan` | `Start`, `Length` (offset en caracteres UTF-16), `Line`, `Column` (base 0). |

## 5. Enlaces (`Domain.Links`)

```csharp
public enum LinkKind { WikiLink, MarkdownLink, Embed, MarkdownEmbed, FrontmatterLink }
public sealed record Link(
    LinkKind Kind,
    string RawTarget,           // tal cual aparece: "Carpeta/Nota"
    string? Subpath,            // "#Encabezado" o "#^bloque" (sin '#')
    SubpathKind SubpathKind,    // None | Heading | Block
    string? Alias,              // texto tras '|' o texto del enlace markdown
    SourceSpan Span,            // rango completo "[[...]]" en el documento
    SourceSpan TargetSpan);     // rango solo del destino (para reescritura precisa)
```

### 5.1 `LinkResolver` (servicio de dominio)

Resuelve `RawTarget` → `VaultPath?` siguiendo las reglas de Obsidian (detalladas en [05](05-markdown-y-enlaces.md) §3). Entrada: el link, la ruta de la nota origen y un `IVaultFileCatalog` (interfaz de dominio, solo lectura, con búsqueda por nombre y por ruta, implementada en Aplicación sobre la caché de metadatos). Resultado: `Resolved(path)` | `Unresolved` | `Ambiguous(paths)` (Ambiguous resuelve igualmente al candidato preferente pero se reporta).

### 5.2 `LinkRewriter` (servicio de dominio)

Dado un documento, la lista de `Link` y un mapa de renombrados `VaultPath viejo → VaultPath nuevo`, produce el texto nuevo con los destinos reescritos **solo** en `TargetSpan`, preservando alias, subpath y formato del enlace (wikilink vs markdown, con/sin extensión, ruta mínima vs completa según `VaultSettings`). Debe ser idempotente y no tocar enlaces dentro de bloques de código.

### 5.3 `LinkGraph`

Proyección inmutable `fuente → destinos` y `destino → fuentes` (backlinks), incluyendo enlaces no resueltos (destino = `UnresolvedTarget(name)`). Usada por el panel de backlinks y la vista de grafo. Construida incrementalmente por la caché de metadatos (Aplicación).

## 6. Etiquetas (`Domain.Tags`)

- `Tag`: nombre sin `#`, segmentos separados por `/`. Válido: letras Unicode, dígitos, `_`, `-`, `/`; DEBE contener al menos un carácter no numérico. Comparación **insensible a mayúsculas** (como Obsidian); se conserva la grafía de la primera aparición para mostrar.
- `TagHierarchy`: árbol con contadores agregados (`#a/b` cuenta para `a` y `a/b`).

## 7. Búsqueda (`Domain.Search`)

- `SearchQueryParser.Parse(string) → Result<SearchQuery>`: AST con nodos `Term`, `Phrase`, `Regex`, `And`, `Or`, `Not`, `Group` y operadores de campo `file:`, `path:`, `tag:`, `line:(...)`, `block:(...)`, `section:(...)`, `content:`, `task:`, `task-todo:`, `task-done:`, `[property]`, `[property:value]`. Sintaxis completa en [08](08-persistencia-e-indexacion.md) §4.
- `SearchQuery` DEBE poder convertirse a: (a) una consulta FTS5 de pre-filtrado (en el adaptador de índice, vía un visitor definido en Aplicación) y (b) un `INoteMatcher` puro que evalúa exactamente la consulta sobre una nota (post-filtrado y cálculo de coincidencias con rangos para resaltado).
- `FuzzyMatcher` para el quick switcher y la paleta: puntuación tipo "subsequence con bonificaciones" (inicio de palabra, consecutivos, coincidencia de mayúsculas), devuelve índices de caracteres coincidentes. Determinista y probado con property tests (p. ej. una coincidencia exacta siempre puntúa ≥ cualquier coincidencia parcial).

## 8. Grafo (`Domain.Graph`)

- `GraphModel`: nodos (`Note`, `Attachment`, `Unresolved`, `Tag`) y aristas (`Link`, `Embed`, `TagEdge`).
- `GraphBuilder` aplica filtros: `ShowOrphans`, `ShowAttachments`, `ShowUnresolved`, `ShowTags`, filtro por `SearchQuery`, profundidad (grafo local, BFS a N saltos desde una nota, por enlaces entrantes y/o salientes).
- `ForceLayout`: simulación Fruchterman-Reingold con aproximación Barnes-Hut (quadtree) para > 500 nodos. Parámetros: repulsión, fuerza de enlace, distancia de enlace, gravedad al centro. **Determinista** dada una semilla (se recibe un `IRandom` sembrado). Expone `Step(dt)` y `IsStable` (energía cinética < umbral). La animación la dirige la UI.

## 9. Plantillas y notas diarias (`Domain.Templates`)

- `TemplateRenderer.Render(templateText, TemplateContext) → string`. Variables: `{{title}}`, `{{date}}`, `{{time}}`, `{{date:FORMAT}}`, `{{time:FORMAT}}` (FORMAT estilo Moment.js: `YYYY`, `MM`, `DD`, `ddd`, `dddd`, `MMM`, `MMMM`, `HH`, `mm`, `ss`, `[literal]`). Traducción Moment→.NET implementada y probada en dominio (`MomentFormat.ToDotNet` + formateador propio con `CultureInfo` inyectado).
- `DailyNotePolicy`: dado `DateOnly` y ajustes → `VaultPath` de la nota diaria (p. ej. `Diario/2026/2026-10-19.md` con formato `YYYY/YYYY-MM-DD`).

## 10. Texto (`Domain.Text`)

- `LineDiff`: algoritmo de **Myers O(ND)** sobre secuencias de líneas (comparación ordinal; con opción de ignorar diferencias de fin de línea). Produce `DiffHunk[]`.
- `ThreeWayTextMerger.Merge(base, ours, theirs) → MergeResult`:
  - Algoritmo **diff3** sobre líneas: calcula diff base→ours y base→theirs, alinea regiones estables e inestables.
  - Regiones cambiadas solo en un lado → se toma ese lado.
  - Cambiadas en ambos lados de forma idéntica → se toma una vez.
  - Cambiadas en ambos de forma distinta → `ConflictRegion`.
  - Resultado: `Clean(text)` o `Conflicted(text con marcadores, regions[])`. Marcadores: `<<<<<<< {etiquetaLocal}` / `=======` / `>>>>>>> {etiquetaRemota}` (configurable).
  - Preserva el final de línea predominante de `ours`.
  - **Caso especial frontmatter**: si el conflicto cae solo dentro del frontmatter y ambos lados son YAML válido con conjuntos de claves modificadas disjuntos, se fusiona a nivel de clave (fusión estructural) — implementado vía un `IFrontmatterMerger` del dominio que trabaja sobre `Frontmatter` ya analizado (el análisis lo hace el adaptador; el dominio recibe valores).
- Propiedades que DEBEN verificarse con FsCheck (ver [09](09-estrategia-de-pruebas.md)):
  - `Merge(b, x, b) == Clean(x)`; `Merge(b, b, y) == Clean(y)`; `Merge(b, x, x) == Clean(x)`.
  - Si `Clean`, el resultado contiene todas las líneas añadidas por ambos lados.
  - `LineDiff.Apply(a, Diff(a, b)) == b` para cualesquiera `a`, `b`.

## 11. Sincronización (`Domain.Sync`)

El detalle funcional está en [06](06-sincronizacion-github.md). Tipos de dominio:

| Tipo | Descripción |
|------|-------------|
| `BlobSha` | SHA-1 hex de 40 caracteres en minúsculas, formato objeto blob de Git. |
| `CommitSha`, `TreeSha` | Igual, tipados por separado. |
| `GitBlobHasher` | `BlobSha Compute(ReadOnlySpan<byte>)` = SHA1(`"blob " + len + "\0" + bytes`). Puro (usa `System.Security.Cryptography.SHA1`, que es BCL y determinista; se permite). Variante incremental por streaming (`IncrementalHash`). |
| `ManifestEntry` | `Path: VaultPath`, `Sha: BlobSha`, `Size: long`, `Mode` (`File` = 100644; los ejecutables 100755 se tratan como fichero; symlinks y submódulos se **ignoran** y se registran en el log). |
| `Manifest` | Diccionario inmutable `VaultPath → ManifestEntry`, con detección de colisiones por `CaseFoldKey`. |
| `SyncBase` | `CommitSha?` + `Manifest` del último estado sincronizado (vacío y `null` antes de la primera sincronización). |
| `IgnoreRules` | Patrones estilo `.gitignore` (subconjunto: `*`, `**`, `?`, `[abc]`, `!negación`, `/` anclado, sufijo `/` = solo directorios). `bool IsIgnored(VaultPath, bool isDirectory)`. Reglas por defecto + fichero `.basalt/syncignore` del vault + exclusiones del perfil local. |
| `ChangeSet` | Diferencia entre dos manifiestos: `Added`, `Modified`, `Deleted` (+ `Renamed` detectados por igualdad de SHA, informativo). |
| `SyncPlanner` | `SyncPlan Plan(SyncBase base, Manifest local, Manifest remote, SyncPolicy policy)`. Totalmente puro. Tabla de decisión en [06](06-sincronizacion-github.md) §5. |
| `SyncPlan` | Lista ordenada de `SyncAction`: `Download(path, remoteSha)`, `Upload(path, localSha)`, `DeleteLocal(path, expectedLocalSha)`, `DeleteRemote(path)`, `MergeText(path, baseSha, localSha, remoteSha)`, `CreateConflictCopy(path, side, sha)`, `NoOp`. Más `ExpectedNewBase` (manifiesto resultante esperado). |
| `SyncPolicy` | Política de conflictos de texto, de binarios, de borrado-vs-edición, límites de tamaño. |
| `ConflictCopyNamer` | Genera nombres de copia de conflicto únicos: `Nombre (conflict 2026-10-19 154233 MacBook).md`; si existe, añade ` 2`, ` 3`… Recibe el conjunto de rutas existentes, el instante y el nombre del dispositivo. Respeta las reglas de `VaultPath` (sustituye caracteres inválidos del nombre del dispositivo por `-`). |
| `SyncOutcome` | Resumen: contadores por tipo de acción, conflictos generados, commit creado, duración. |

## 12. Cuentas (`Domain.Accounts`)

- `GitHubAccountId` (Guid local), `GitHubLogin` (string), `GitHubHost` (por defecto `https://github.com`, API `https://api.github.com`; soporte GitHub Enterprise Server con API `https://{host}/api/v3`).
- `GitHubAccount`: `Id`, `Login`, `Host`, `AvatarUrl`, `Scopes`, `AddedAt`. El **token NO forma parte de la entidad** (vive en `ISecretStore` con clave `github-token:{accountId}`).

## 13. Invariantes globales del dominio

- I1. Dos ficheros de un mismo vault no pueden tener la misma `CaseFoldKey` (al crear/renombrar se rechaza con `path.case_collision`; si llegan por sincronización se resuelve como conflicto, ver [06](06-sincronizacion-github.md) §7.4).
- I2. Ninguna operación de dominio produce texto de nota que pierda contenido no relacionado con la operación (verificado por property tests de `LinkRewriter`: el texto fuera de `TargetSpan` es idéntico byte a byte).
- I3. `SyncPlanner` nunca produce un plan que elimine contenido sin que exista una copia (en remoto, en local, o como copia de conflicto) — verificado con property tests.
