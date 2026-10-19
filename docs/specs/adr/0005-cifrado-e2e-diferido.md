# 0005 — Cifrado de extremo a extremo diferido

- Estado: Aceptado
- Fecha: 2026-10-19

## Contexto
Los vaults se almacenan en repositorios de GitHub (privados por defecto). Algunos usuarios podrían querer que GitHub no pueda leer el contenido.

## Decisión
No se implementa cifrado E2E en v1. Se crean repos privados por defecto y se avisa si el repo es público.

## Consecuencias
- El contenido es legible en github.com (ventaja: historial y diffs legibles).
- Extensión futura: un decorador de `IRemoteRepository` (`EncryptingRemoteRepository`) que cifre blobs y nombres con una clave derivada de una frase de paso, sin cambios en el núcleo; implicaría perder la fusión de tres vías basada en contenido remoto legible solo si se cifra de forma no determinista (a diseñar en su propio ADR).
