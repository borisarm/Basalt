# 0014 — Un JSON compartido inválido se aparta antes de sobrescribirlo

- Estado: Aceptado
- Fecha: 2026-10-20

## Contexto
08 §1.1: si un fichero JSON del vault es inválido (p. ej. tras un conflicto de sync con marcadores `<<<<<<<`), se usa el valor por defecto, se avisa y **no se sobrescribe hasta que el usuario cambie algo**. Pero cuando el usuario cambia algo (añade un marcador), escribir encima perdería el contenido inválido, que puede contener las dos versiones del conflicto. Eso choca con la regla "nunca perder datos".

## Decisión
- Al guardar un documento compartido cuyo contenido actual es inválido (`bookmarks.json`; en adelante cualquier otro de `.basalt/` que siga esta regla), el adaptador **mueve antes el fichero inválido** a `.basalt/{nombre}.invalid.json` (sufijo ` 2`, ` 3`… si existe) y después escribe el documento nuevo.
- Un documento con un tipo de entrada desconocido (escrito por una versión más nueva) se trata como inválido: no se reinterpreta parcialmente, así que se conserva entero.

## Consecuencias
- ✅ El usuario puede recuperar el contenido anterior a mano. La sincronización sube el fichero apartado como cualquier otro, así que tampoco se pierde en otros dispositivos.
- ⚠️ Pueden acumularse ficheros `*.invalid*.json` en `.basalt/` hasta que el usuario los borre.
