# 0010 — Módulo de dominio `Metadata` para `NoteMetadata`

- Estado: Aceptado
- Fecha: 2026-10-19

## Contexto
02 §4 sitúa `NoteMetadata` (frontmatter, encabezados, enlaces, embeds, etiquetas, bloques, tareas, alias, recuento de palabras) en `Domain.Notes`, pero la matriz de módulos de 09 §8 solo permite que `Notes` dependa de `Common`. `NoteMetadata` necesita `Link` (módulo `Links`) y `Tag` (módulo `Tags`), que a su vez dependen de `Notes`: colocarlo en `Notes` crearía un ciclo.

## Decisión
Nuevo módulo `Basalt.Domain.Metadata` con `NoteMetadata`, `Frontmatter`, `TagOccurrence`, `BlockReference` y `TaskItem`. Dependencias permitidas: `Common, Notes, Links, Tags`. `Search` y `Graph` pueden depender de `Metadata`. Se actualiza la tabla de 09 §8.

## Consecuencias
- ✅ Sin ciclos entre módulos; `Notes` sigue siendo la base (rutas, contenido, encabezados, bloques).
- ⚠️ `NoteMetadata` vive en `Basalt.Domain.Metadata` en lugar de `Basalt.Domain.Notes` (cambio de namespace respecto a 02 §4).
