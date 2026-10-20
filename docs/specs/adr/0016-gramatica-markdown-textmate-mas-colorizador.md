# 0016 — Resaltado: gramática Markdown de TextMateSharp más un colorizador para la sintaxis de Obsidian

- Estado: Aceptado
- Fecha: 2026-10-20

## Contexto
07 §5.1 pide resaltado con `AvaloniaEdit.TextMate` y una gramática propia (`basalt-markdown.tmLanguage.json`) con inyecciones para wikilinks, etiquetas, resaltados, comentarios y callouts. `AvaloniaEdit.TextMate` ya incluye, vía `TextMateSharp.Grammars`, la gramática Markdown y temas claro y oscuro mantenidos por terceros. Escribir y mantener una gramática TextMate completa propia es costoso y frágil.

## Decisión
- Se usa la gramática Markdown de `TextMateSharp.Grammars`, con los temas Light+ y Dark+ según la variante del tema de Avalonia.
- La sintaxis de Obsidian que esa gramática no conoce (`[[enlace]]`, `![[embed]]`, `#etiqueta`, `==resaltado==`, `%%comentario%%`) la colorea `ObsidianSyntaxColorizer`, un `DocumentColorizingTransformer` que usa los tokens de diseño.
- El plegado (encabezados y bloques cercados) lo calcula el propio control.

## Consecuencias
- ✅ Sin ficheros de gramática propios que mantener; colores de la sintaxis propia desde los tokens del tema.
- ⚠️ Los callouts se ven como citas normales en el modo código (en el modo lectura sí se representan, 07 §6).
- ⚠️ El resaltado de etiquetas usa una expresión aproximada a la de 05 §4; el análisis real de etiquetas sigue siendo el del adaptador Markdown.
