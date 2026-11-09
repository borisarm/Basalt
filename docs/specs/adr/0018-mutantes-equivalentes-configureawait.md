# 0018 — Mutantes equivalentes de `ConfigureAwait`

## Contexto

09 §10 fija la puerta de mutación de `Basalt.Application` en ≥ 70 % (break), pero no dice cómo tratar los
mutantes **equivalentes** (los que no cambian el comportamiento observable y, por tanto, ningún test puede
matar). La capa de aplicación es casi toda asíncrona y usa `ConfigureAwait(false)` en cada `await`
(ver 13). Stryker muta ese argumento a `true`; sin `SynchronizationContext` (como en los tests y en
el núcleo, que no depende de UI) el resultado es idéntico. En una ejecución completa (Linux, mismo SDK que CI), 337 de los
815 mutantes supervivientes de `Basalt.Application` eran exactamente esta mutación.

`Basalt.Domain` ya trata el mismo caso como equivalente con un comentario
`// Stryker disable once Boolean : ConfigureAwait(false) is not observable by tests (equivalent mutant)`.

## Decisión

`tests/Basalt.Application.Tests/stryker-config.json` declara `"ignore-methods": [ "ConfigureAwait" ]`:
Stryker no muta la llamada `ConfigureAwait(...)` ni sus argumentos. El resto de mutaciones de la misma
sentencia (por ejemplo, eliminar el `await` completo) se siguen generando y deben matarse.

El umbral **no cambia** (≥ 70 % break). Solo se excluyen mutantes demostrablemente equivalentes; cualquier
otra exclusión necesita su propio comentario `Stryker disable` justificado o un ADR.

## Consecuencias

- La puntuación mide solo mutantes que un test puede detectar.
- No hace falta un comentario por cada `await` en la capa de aplicación.
- Si algún día el núcleo dependiera de un `SynchronizationContext`, habría que revisar esta decisión.
