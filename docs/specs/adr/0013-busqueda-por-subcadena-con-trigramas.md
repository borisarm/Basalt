# 0013 — Búsqueda por subcadena: FTS5 `trigram` sobre texto plegado

- Estado: Aceptado
- Fecha: 2026-10-20

## Contexto
08 §4.2 define `term` como "el contenido o el nombre **contiene** `term`" (como Obsidian: `puesto` encuentra `presupuesto`), y 08 §4.3 exige que `SqliteNoteIndex.Search` devuelva exactamente lo mismo que el oráculo lineal (`InMemoryNoteIndex` con `NoteMatcher`). El esquema original usaba el tokenizador `unicode61` con prefijos, que solo encuentra palabras completas o prefijos: como pre-filtro **infra-aproximaría** (perdería resultados) y rompería la equivalencia.

## Decisión
- `content_fts` usa `tokenize = 'trigram case_sensitive 1'` y guarda la ruta, el nombre de fichero y el texto **plegados** con `FoldedText` del dominio (minúsculas invariantes, sin marcas combinantes) de todos los ficheros, incluidos los adjuntos (texto vacío).
- Un término o frase de ≥ 3 caracteres se traduce a una frase trigram: con el mismo plegado en ambos lados, equivale exactamente a "contiene la subcadena". Los de < 3 caracteres no restringen.
- El texto original pasa a la tabla `contents` (para `GetAsync` y para calcular rangos de resaltado exactos).
- `tag:` y `[propiedad]` se pre-filtran con las tablas `tags` (nueva columna `tag_search` con el mismo plegado) y `properties`.
- La versión del esquema pasa a 2. Como el índice es una caché, una base con otra versión se descarta y se reconstruye.
- Se probó `detail = column` con AND de trigramas: no mejoraba el tiempo (domina la lectura del texto de los candidatos) y multiplicaba los candidatos de las frases, así que se descartó.

## Consecuencias
- ✅ Equivalencia exacta con el oráculo, verificada por un test de propiedades con corpus y consultas aleatorias.
- ✅ Un único plegado (dominio) para el pre-filtro y la evaluación exacta.
- ⚠️ El índice FTS trigram es mayor que el de `unicode61` (aceptable: vive fuera del vault, en `{AppData}`).
- ⚠️ En una búsqueda de una palabra presente en casi todas las notas, el coste lo domina la lectura del texto de los candidatos (≈ 0,2 s en el vault de referencia). Hay margen respecto a F-007.5 (< 300 ms), con un test `Category=Performance` que lo vigila.
