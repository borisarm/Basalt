# 05 — Markdown, enlaces, etiquetas y frontmatter

Objetivo: **compatibilidad de formato con Obsidian**. Un vault editado en Basalt debe interpretarse igual en Obsidian.

## 1. Dialecto soportado

Base: **CommonMark** + **GFM** (tablas, tachado `~~`, autolinks, listas de tareas, notas al pie) mediante Markdig con `UseAdvancedExtensions()` restringido a: `PipeTables`, `TaskLists`, `Footnotes`, `AutoLinks`, `EmphasisExtras` (strikethrough), `MathBlocks` (`$...$`, `$$...$$` — se renderizan como bloque de código monoespaciado en MVP), `YamlFrontMatter`.

Extensiones propias de Obsidian (implementadas como `InlineParser`/`BlockParser` de Markdig en `Basalt.Adapters.Markdown`):

| Sintaxis | Elemento |
|----------|----------|
| `[[destino]]`, `[[destino\|alias]]`, `[[destino#Encabezado]]`, `[[destino#^bloque]]`, `[[#Encabezado]]` (misma nota) | Wikilink |
| `![[destino]]`, `![[imagen.png\|300]]`, `![[imagen.png\|300x200]]`, `![[nota#Sección]]` | Embed |
| `#etiqueta`, `#a/b/c` | Etiqueta en línea |
| `^id-bloque` al final de línea/párrafo | Id de bloque |
| `==resaltado==` | Resaltado |
| `%%comentario%%` (en línea y bloque multilínea) | Comentario (oculto en lectura, no indexado como texto visible pero sí buscable) |
| `> [!tipo] Título` + `> contenido`, `> [!tipo]-` plegado, `> [!tipo]+` desplegado | Callout. Tipos: note, abstract/summary/tldr, info, todo, tip/hint/important, success/check/done, question/help/faq, warning/caution/attention, failure/fail/missing, danger/error, bug, example, quote/cite |

**Exclusiones de análisis** (no se extraen enlaces/etiquetas): bloques de código cercados e indentados, código en línea, comentarios `%%` (para enlaces sí: Obsidian no los indexa — tampoco nosotros), bloques math, HTML en bruto, frontmatter (los enlaces del frontmatter se extraen por separado como `FrontmatterLink` solo si un valor string es exactamente `"[[...]]"`).

## 2. Sintaxis de wikilink — gramática

```
wikilink   = "[[" target [ "#" subpath ] [ "|" alias ] "]]"
embed      = "!" wikilink
target     = 1*( any-char excepto "[", "]", "|", "#", "^" , salto de línea )   ; puede estar vacío si hay subpath (enlace interno)
subpath    = heading-path / "^" block-id
heading-path = heading *( "#" heading )        ; "A#B#C" = encabezado C bajo B bajo A
block-id   = 1*( ALPHA / DIGIT / "-" )
alias      = 1*( any-char excepto "]]" y salto de línea )
```

- Escapado: `\|` dentro de tablas se trata como `|` del alias (comportamiento de Obsidian en tablas).
- Espacios iniciales/finales en `target` y `alias` se recortan para resolver pero se preservan en el texto.
- El target PUEDE incluir extensión (`[[Nota.md]]`, `[[imagen.png]]`). Si no tiene extensión, se asume `.md`.

## 3. Resolución de enlaces (LinkResolver)

Dado `target` (sin subpath) desde la nota `source`:

1. Normalizar: recortar, sustituir `\` por `/`, decodificar `%20` y demás percent-encoding (solo en enlaces Markdown), NFC.
2. Si `target` está vacío → la propia nota `source` (enlace interno a encabezado/bloque).
3. Si `target` contiene `/`:
   1. Si empieza por `./` o `../` → relativa a la carpeta de `source`.
   2. En otro caso, probar como ruta absoluta desde la raíz del vault (con `.md` añadido si no hay extensión).
   3. Si no existe, probar como sufijo de ruta: candidatos cuya ruta termina en `/target(.md)`. Si hay uno → ese. Si varios → aplicar desempate (paso 5).
4. Si `target` no contiene `/`: candidatos = todos los ficheros cuyo `Name` (con `.md` implícito) coincide **sin distinguir mayúsculas**.
5. Desempate entre varios candidatos (Obsidian "shortest path when possible"):
   1. Coincidencia exacta de mayúsculas antes que insensible.
   2. Fichero en la misma carpeta que `source`.
   3. Ruta más corta (menos segmentos).
   4. Orden ordinal de la ruta.
6. Sin candidatos → `Unresolved(target)`.

**Alias del frontmatter** NO participan en la resolución de `[[...]]` (igual que Obsidian): solo en el autocompletado (que inserta `[[Nota real|alias]]`).

### 3.1 Generación de enlaces (al autocompletar o reescribir)

Según `VaultSettings.LinkPathStyle`:
- `Shortest` (por defecto): solo el nombre si es único en el vault; si no, el sufijo de ruta más corto que lo hace único.
- `Relative`: ruta relativa a la nota origen.
- `Absolute`: ruta desde la raíz.

Y `VaultSettings.LinkFormat`: `Wikilink` (`[[...]]`) o `Markdown` (`[alias](ruta%20codificada.md)`). En wikilinks se omite `.md`; en Markdown se incluye y se codifican los espacios como `%20`.

### 3.2 Anclas de encabezado

- La comparación de `#Encabezado` con encabezados reales es por texto normalizado: se eliminan los caracteres `# | ^ : % [ ] \`, se colapsan espacios, comparación insensible a mayúsculas.
- En la reescritura al renombrar un **encabezado** (fuera de alcance MVP: no se actualizan enlaces al renombrar encabezados; DEBE quedar como TODO documentado).

### 3.3 Enlaces Markdown

- `[texto](destino)` con destino sin esquema (`http:`, `https:`, `mailto:`, `obsidian:`, `file:` se tratan como externos) se resuelve como enlace interno: ruta relativa a la nota origen primero; luego absoluta desde la raíz; luego por nombre (como wikilink).
- `<destino con espacios.md>` (destino entre ángulos) soportado.

## 4. Etiquetas

Expresión regular de referencia (aplicada solo a texto fuera de exclusiones):

```
(?<=^|\s|[(\[{,;])#(?<tag>[\p{L}\p{M}\p{N}_/\-]*[\p{L}_/\-][\p{L}\p{M}\p{N}_/\-]*)
```

(`\p{M}`: las marcas combinantes de letras escritas en NFD forman parte de la letra.)

- Debe contener al menos un carácter no numérico.
- No se reconoce si el `#` va precedido de un carácter de palabra (`C#`, `a#b`).
- Termina en el primer carácter fuera del conjunto permitido.
- `/` final se elimina (`#a/` → `a`).
- Frontmatter: `tags` o `tag` como string separado por comas/espacios o lista YAML; el `#` inicial es opcional.

## 5. Frontmatter

- Solo si el documento empieza **exactamente** en la línea 1 con `---` y existe una línea de cierre `---` (o `...`).
- Se analiza con YamlDotNet en modo seguro (sin tags de tipo arbitrarios). Tipos inferidos: `null`, `bool`, `number` (int/decimal), `date` (`YYYY-MM-DD`), `datetime` (ISO 8601), `string`, `list`, `map` (se conserva pero el panel lo muestra como texto YAML).
- **Edición preservando formato**: `IFrontmatterEditor` modifica solo la(s) línea(s) de la clave afectada cuando es un escalar o lista en bloque; si la estructura no permite edición quirúrgica (map anidado, flujo complejo), regenera el bloque YAML completo con estilo canónico (claves en el orden original). Tests de snapshot cubren ambos casos.

## 6. Ids de bloque

- Patrón: `\s\^([A-Za-z0-9-]+)$` al final de la última línea de un párrafo, elemento de lista o línea de tabla; o en una línea propia `^id` tras un bloque (lista, cita, tabla).
- Generación: 6 caracteres `[a-z0-9]` vía `IRandom`; garantizado único dentro de la nota.

## 7. Recuento de palabras

Palabras = secuencias de letras/dígitos separadas por espacio o puntuación, excluyendo frontmatter, sintaxis Markdown y bloques de código; los caracteres CJK cuentan como una palabra cada uno.

## 8. Conjunto de pruebas de referencia (golden files)

`tests/Basalt.Adapters.Markdown.Tests/Golden/` DEBE contener al menos 60 documentos `.md` de entrada con su salida `NoteMetadata` serializada en JSON verificada con **Verify** (`*.verified.json`), cubriendo:

- Todos los ejemplos de esta especificación.
- Casos límite: wikilink sin cerrar, `[[]]`, `[[|alias]]`, enlaces anidados en tablas, en citas, en callouts, en listas profundas, en notas al pie, junto a código en línea, tras `\[`, frontmatter vacío, frontmatter no cerrado, BOM, CRLF, mezcla de finales de línea, texto RTL, emojis y combinaciones de caracteres (surrogates) — los `SourceSpan` deben ser correctos en UTF-16.
- Un documento grande (≥ 1 MB) para pruebas de rendimiento (análisis < 50 ms).
