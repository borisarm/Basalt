# 0008 — Compilar con el SDK de .NET 11 manteniendo el TFM `net10.0`

- Estado: Aceptado
- Fecha: 2026-10-19

## Contexto
La especificación (12 §2) fija `global.json` en el SDK `10.0.100`. La máquina de desarrollo solo tiene instalado el SDK `11.0.100-rc.2` (con los runtimes 8.0, 10.0.13 y 11.0 RC). El propietario del proyecto pidió usar el SDK 11.

## Decisión
- `global.json` fija `11.0.100-rc.2.26504.105` con `rollForward: latestFeature` y `allowPrerelease: true`, y selecciona `Microsoft.Testing.Platform` como runner de `dotnet test`.
- El **TFM sigue siendo `net10.0`** (y `net10.0-windows` / `net10.0-ios`): el producto apunta al runtime LTS que manda la especificación; los tests se ejecutan sobre el runtime 10.0.x instalado.
- Se desarrolla primero la versión Windows; macOS/iOS se compilan en CI (`macos-latest`).

## Consecuencias
- ✅ Sin cambio de comportamiento del producto ni de requisitos de runtime.
- ⚠️ El SDK es una versión preliminar: cuando salga 11.0.100 GA, `latestFeature` lo seleccionará automáticamente.
- ⚠️ Los workloads de iOS instalados son los de .NET 11; el head iOS puede necesitar el workload de .NET 10 o pasar a `net11.0-ios` (se decidirá en la fase 10 con su propio ADR).

## Alternativas consideradas
- Instalar el SDK 10: descartado por indicación del propietario.
- Cambiar el TFM a `net11.0`: innecesario y se aleja de la especificación (LTS).
