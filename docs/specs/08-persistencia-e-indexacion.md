# 08 — Persistencia, indexación y búsqueda

## 1. Qué se guarda y dónde

| Dato | Ubicación | ¿En el repo? | Formato |
|------|-----------|--------------|---------|
| Notas y adjuntos | Carpeta del vault | Sí | Ficheros del usuario |
| Identidad y ajustes compartidos del vault | `<vault>/.basalt/vault.json` | Sí | JSON (versionado con `schemaVersion`) |
| Tipos de propiedades | `<vault>/.basalt/types.json` | Sí | JSON |
| Marcadores | `<vault>/.basalt/bookmarks.json` | Sí | JSON |
| Exclusiones de sync compartidas | `<vault>/.basalt/syncignore` | Sí | gitignore |
| Espacio de trabajo por dispositivo | `<vault>/.basalt/workspace.{deviceId}.json` | No (ignorado siempre) | JSON |
| Papelera | `<vault>/.trash/` | Configurable (no por defecto) | Ficheros |
| Registro de vaults del dispositivo | `{AppData}/vaults.json` | — | JSON |
| Ajustes de la app | `{AppData}/settings.json` | — | JSON |
| Cuentas (sin token) | `{AppData}/accounts.json` | — | JSON |
| Tokens | Almacén seguro del SO | — | — |
| Índice | `{AppData}/vaults/{vaultId}/index.db` | — | SQLite |
| Estado de sync | `{AppData}/vaults/{vaultId}/sync/` | — | Ver [06](06-sincronizacion-github.md) §4 |
| Recientes | `{AppData}/vaults/{vaultId}/recent.json` | — | JSON |
| Logs | `{AppData}/logs/basalt-YYYYMMDD.log` | — | Texto (Serilog), 7 días |
| Device id | `{AppData}/device.json` | — | JSON (`DeviceId`, `DisplayName`) |

### 1.1 Reglas para ficheros JSON propios
- Serialización con `System.Text.Json` source-generated, `camelCase`, indentado para los que están en el vault (legibles en diffs de GitHub), compacto para los de `{AppData}`.
- Todos llevan `schemaVersion` (int). Migraciones explícitas en `Basalt.Adapters.Platform/Migrations` con tests por versión.
- Escritura atómica (temp + rename). Lectura tolerante: si un fichero del vault es inválido (p. ej. tras un conflicto con marcadores), se usa el valor por defecto, se registra un aviso visible y **no se sobrescribe** hasta que el usuario cambie algo.
- `bookmarks.json` y `types.json` se fusionan de forma estructural ante conflictos de sync si ambos lados son JSON válido (mismo mecanismo que el frontmatter: `IJsonStructuralMerger` en dominio, a nivel de elemento/clave).

## 2. Índice SQLite

### 2.1 Esquema

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA foreign_keys = ON;

CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);   -- schema_version, analyzer_version

CREATE TABLE files (
  id INTEGER PRIMARY KEY,
  path TEXT NOT NULL UNIQUE,            -- VaultPath canónico
  path_fold TEXT NOT NULL,              -- CaseFoldKey
  name TEXT NOT NULL,                   -- sin extensión para notas
  ext TEXT NOT NULL,
  kind INTEGER NOT NULL,
  size INTEGER NOT NULL,
  mtime_utc INTEGER NOT NULL,           -- ticks
  ctime_utc INTEGER,
  sha TEXT,                             -- BlobSha
  word_count INTEGER
);
CREATE INDEX ix_files_name ON files(name COLLATE NOCASE);
CREATE INDEX ix_files_path_fold ON files(path_fold);

CREATE TABLE links (
  source_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
  kind INTEGER NOT NULL,
  raw_target TEXT NOT NULL,
  resolved_id INTEGER REFERENCES files(id) ON DELETE SET NULL,
  subpath TEXT, alias TEXT,
  line INTEGER NOT NULL, col INTEGER NOT NULL, start INTEGER NOT NULL, length INTEGER NOT NULL
);
CREATE INDEX ix_links_resolved ON links(resolved_id);
CREATE INDEX ix_links_raw ON links(raw_target COLLATE NOCASE);

CREATE TABLE tags (file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                   tag TEXT NOT NULL, tag_fold TEXT NOT NULL, line INTEGER, source INTEGER NOT NULL); -- 0 texto, 1 frontmatter
CREATE INDEX ix_tags_fold ON tags(tag_fold);

CREATE TABLE headings (file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                       level INTEGER NOT NULL, text TEXT NOT NULL, line INTEGER NOT NULL);
CREATE TABLE blocks   (file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                       block_id TEXT NOT NULL, line INTEGER NOT NULL);
CREATE TABLE aliases  (file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE, alias TEXT NOT NULL);
CREATE TABLE properties (file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                         key TEXT NOT NULL, key_fold TEXT NOT NULL, value_json TEXT NOT NULL, value_text TEXT);
CREATE TABLE tasks (file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                    line INTEGER NOT NULL, done INTEGER NOT NULL, text TEXT NOT NULL);

CREATE VIRTUAL TABLE content_fts USING fts5(
  path, name, body, tokenize = 'unicode61 remove_diacritics 2', prefix = '2 3'
);  -- rowid = files.id
```

- El texto completo NO se duplica fuera de FTS: `body` es el contenido íntegro (para post-filtrado se lee de FTS con `SELECT body` o del disco si se necesita exactitud de offsets).
- Versión del analizador (`analyzer_version`) en `meta`: si cambia (nueva versión de la app con reglas de análisis distintas) → reindexado completo en segundo plano.

### 2.2 Indexación

- **Al abrir el vault**: escaneo de ficheros; se compara `(path, size, mtime)` con `files`; solo se analizan los nuevos/cambiados; se eliminan los ausentes. Se procesa en lotes de 200 en una transacción, en un hilo de fondo con prioridad baja, notificando progreso. La UI es usable desde el primer momento (el explorador sale del escaneo; búsqueda y backlinks muestran "Indexando… n/m").
- **En caliente**: suscripción a `NoteSaved`, `EntryRenamed`, `EntriesDeleted`, `FilesChangedBySync`, `VaultFileChangedExternally` → reindexado del fichero afectado (debounce 300 ms por ruta).
- **Resolución de enlaces**: `links.resolved_id` se recalcula cuando cambia el conjunto de ficheros (altas/bajas/renombrados): se recalculan solo los enlaces cuyo `raw_target` coincide (por nombre) con el fichero afectado.
- Coherencia: `IMetadataCache` (memoria) es la fuente para la UI; el índice persistente acelera el arranque. Ante discrepancia (detectada por `sha`), manda el fichero en disco.

## 3. Caché de metadatos en memoria (`IMetadataCache`)

- Mapas: `path → NoteMetadataSummary`, `nameFold → paths`, `aliasFold → paths`, `tagFold → paths`, `LinkGraph`.
- Construida desde el índice al abrir (< 1 s para 10 000 notas) y actualizada por eventos.
- Thread-safe: estructuras inmutables (`ImmutableDictionary`) con reemplazo atómico de referencia (lecturas sin bloqueo).
- Emite `MetadataChanged(paths)` para refrescar paneles.

## 4. Lenguaje de búsqueda

### 4.1 Gramática

```
query      = and-expr
and-expr   = or-expr *( [ "AND" ] or-expr )      ; espacio = AND implícito
or-expr    = unary *( "OR" unary )               ; OR liga más que AND
unary      = [ "-" ] primary
primary    = "(" query ")" / field / regex / phrase / term / property
field      = field-name ":" ( primary )           ; file: path: tag: line: block: section: content: task: task-todo: task-done:
property   = "[" key [ ":" ( value / "null" ) ] "]"
regex      = "/" pattern "/"
phrase     = DQUOTE *char DQUOTE
term       = 1*( char excepto espacio, '"', '(', ')' )
```

> Precedencia: como en Obsidian, `OR` tiene **más** precedencia que el AND (implícito o explícito): `a b OR c` = `a AND (b OR c)`. Esta regla DEBE estar cubierta por tests explícitos.
>
> Análisis tolerante (búsqueda mientras se escribe): frases, grupos y propiedades sin cerrar llegan hasta el final; `)` sin pareja y `AND`/`OR` colgantes se ignoran; grupos y campos vacíos se descartan; `OR`/`AND` solo son operadores en mayúsculas; `campo:` desconocido es un término literal; `/x/` es regex solo si la barra de cierre va seguida de un delimitador. Errores: `search.invalid_regex` (patrón no válido o no soportado por `NonBacktracking`) y `search.too_complex` (anidamiento > 64).

### 4.2 Semántica

| Operador | Significado |
|----------|-------------|
| `term` | El contenido o el nombre contiene `term` (insensible a mayúsculas y diacríticos por defecto) |
| `"frase exacta"` | Contiene la secuencia literal |
| `/regex/` | .NET `Regex` con `RegexOptions.NonBacktracking` (protección contra ReDoS); timeout 200 ms por nota |
| `file:x` | El nombre del fichero contiene x |
| `path:x` | La ruta contiene x |
| `tag:#x` | Tiene la etiqueta x o una descendiente (`tag:#a` encuentra `#a/b`) |
| `line:(a b)` | a y b en la misma línea |
| `block:(a b)` | en el mismo bloque (párrafo, elemento de lista…) |
| `section:(a b)` | en la misma sección (entre encabezados) |
| `content:x` | Solo en el contenido (no el nombre) |
| `task:x` / `task-todo:x` / `task-done:x` | En tareas (todas / pendientes / hechas) |
| `[clave]` | Tiene la propiedad |
| `[clave:valor]` | La propiedad contiene el valor (`[status:done]`); `[clave:null]` = vacía |
| `-expr` | Negación |

### 4.3 Ejecución

1. `SearchQueryParser` (dominio) → AST.
2. `FtsQueryBuilder` (visitor en el adaptador de índice) → expresión FTS5 que **sobre-aproxima** el conjunto (pre-filtro). Nodos no traducibles (regex, line:, negaciones complejas) → sin restricción en ese subárbol.
3. Para cada candidato: `INoteMatcher` (dominio) evalúa la consulta exacta sobre el contenido y devuelve `MatchRange[]` para resaltado.
4. Orden: relevancia (bm25 del pre-filtro + número de coincidencias), o por nombre / fecha de modificación.
5. Resultados en streaming (`IAsyncEnumerable`) con cancelación al teclear.

**Equivalencia exigida**: para cualquier consulta y corpus, `SqliteNoteIndex.Search` y `InMemoryNoteIndex.Search` (oráculo lineal) DEBEN devolver el mismo conjunto de notas → property test con consultas y corpus aleatorios (ver [09](09-estrategia-de-pruebas.md) §6).

## 5. Papelera

- `DeleteEntries(Mode=Trash)` mueve a `.trash/<ruta-original>`; si existe, sufijo ` 2`, ` 3`…
- Comando "Vaciar papelera" con confirmación (borrado físico). Vista de papelera en ajustes con "Restaurar" (a la ruta original si está libre).
