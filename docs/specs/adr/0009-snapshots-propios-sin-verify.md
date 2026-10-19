# 0009 — Snapshots con un helper propio en lugar de Verify

- Estado: Aceptado
- Fecha: 2026-10-19

## Contexto
La especificación (01 §6, 09 §3) propone `Verify.XunitV3` para tests de snapshot. La versión actual (33.x) incluye una comprobación de licencia (`SponsorCheck`, error SC021) que rompe la compilación salvo que el proyecto declare un patrocinio, una licencia o una exención. Esa declaración es una decisión legal del propietario, no del agente.

## Decisión
Se implementa un helper mínimo de snapshots en `Basalt.Testing` (`Snapshot.Match`): serializa el valor a JSON canónico (indentado, LF), lo compara con `<Clase>.<Método>[.<caso>].verified.json` junto al fichero de test y, si difiere o no existe, escribe `*.received.json` y falla. Para aceptar un cambio se renombra/copia el `.received` a `.verified` (o se ejecuta con `BASALT_ACCEPT_SNAPSHOTS=1`).

## Consecuencias
- ✅ Sin dependencia con requisitos de licencia; misma convención de ficheros `*.verified.*` versionados.
- ⚠️ Sin herramienta de diff integrada (se usa el diff del IDE o de git).
- Si el propietario adquiere licencia de Verify, puede sustituirse sin cambiar los ficheros verificados.
