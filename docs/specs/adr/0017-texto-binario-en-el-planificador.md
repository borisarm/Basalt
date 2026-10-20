# 0017 — Clasificación texto/binario en el planificador

## Contexto

06 §5 define un fichero como texto si su extensión está en una lista **y** su contenido es UTF-8 válido sin
NUL en los primeros 8 KB; si cualquiera de los tres lados (base, local, remoto) no es texto, la fila 5/15 se
resuelve como conflicto binario. Sin embargo, la firma especificada del planificador
(`SyncPlan Plan(SyncBase, Manifest, Manifest, SyncPolicy)`, 02 §11) solo recibe manifiestos, que contienen
ruta, SHA y tamaño, no los bytes. El planificador es puro y no puede leer contenido.

## Decisión

`SyncPlanner` decide texto/binario **solo por extensión** (la misma lista de 06 §5, a través de
`TextBinaryClassifier`). El materializador (T9.3), que sí descarga/stagea los blobs, vuelve a comprobar la
condición completa (`TextBinaryClassifier.IsText` con los bytes de los tres lados); si falla, aplica la
política de conflictos **binarios** a la acción `MergeText`.

Esto es conservador: una fusión de texto solo se intenta cuando la extensión lo permite y, si el contenido
no es texto, nunca se fuerza una fusión; se resuelve con la política binaria (por defecto `KeepBoth`, que
conserva ambas versiones). No se pierde contenido y no se cambia el tamaño ni la pureza del dominio.

El `ExpectedNewBase` del plan es el manifiesto resultante de las acciones deterministas: las rutas que se
fusionan o cuyas copias de conflicto se nombran al materializar conservan el SHA remoto en la ruta (o el
ganador de la política), y las copias de conflicto (SHA desconocido hasta ejecutar) se añaden al confirmar
la base (T9.4). Las propiedades P1-P5 se formulan sobre esa base esperada.

## Consecuencias

- `SyncPlanner` sigue siendo puro y determinista y conserva la firma de la especificación.
- La detección por contenido y la política binaria se prueban en el materializador con recursos reales.
- Un fichero con extensión de texto pero bytes binarios se trata como binario en la aplicación, que es el
  comportamiento seguro.
