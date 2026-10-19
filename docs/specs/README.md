# Basalt — Especificaciones

**Basalt** es un clon de [Obsidian](https://obsidian.md) multiplataforma (Windows, macOS, iOS) construido con **.NET 10** y **Avalonia UI**, con un modelo de **sincronización por vault basado en repositorios de GitHub (un repositorio por vault)**.

Este directorio contiene la especificación completa del producto y del sistema. Está escrita para que un agente de codificación (Claude Code) pueda implementar la aplicación de principio a fin, fase por fase, con pruebas exhaustivas, sin necesidad de decisiones de diseño adicionales.

## Cómo usar estas especificaciones

1. Copia `CLAUDE.md` a la raíz del repositorio de código (será leído automáticamente por Claude Code).
2. Estas especificaciones viven en `docs/specs/` (ADRs en `docs/specs/adr/`).
3. Pide a Claude Code: *"Implementa la siguiente tarea pendiente de `docs/specs/10-plan-de-implementacion.md` siguiendo `CLAUDE.md`"*. Repite hasta completar todas las fases.
4. El progreso se registra en `PROGRESS.md` en la raíz del repositorio (lo crea el agente en la Fase 0).

## Índice

| # | Documento | Contenido |
|---|-----------|-----------|
| — | [CLAUDE.md](../../CLAUDE.md) | Reglas de trabajo para Claude Code (flujo TDD, comandos, Definition of Done) |
| 00 | [Visión y alcance](00-vision-y-alcance.md) | Objetivos, usuarios, alcance MVP / v1, fuera de alcance, glosario |
| 01 | [Arquitectura](01-arquitectura.md) | Monolito hexagonal, proyectos, reglas de dependencia, SOLID, composición |
| 02 | [Modelo de dominio](02-modelo-de-dominio.md) | Entidades, value objects, servicios de dominio, invariantes |
| 03 | [Puertos y adaptadores](03-puertos-y-adaptadores.md) | Contratos C# de todos los puertos de entrada y salida |
| 04 | [Especificación funcional](04-especificacion-funcional.md) | Funcionalidades con criterios de aceptación (Gherkin) |
| 05 | [Markdown y enlaces](05-markdown-y-enlaces.md) | Dialecto soportado, wikilinks, resolución de enlaces, etiquetas, frontmatter |
| 06 | [Sincronización con GitHub](06-sincronizacion-github.md) | Modelo de sincronización por vault, algoritmo, conflictos, autenticación |
| 07 | [Interfaz de usuario (Avalonia)](07-interfaz-usuario.md) | MVVM, vistas, navegación, editor, temas, diferencias por plataforma |
| 08 | [Persistencia e indexación](08-persistencia-e-indexacion.md) | Disco, metadatos, índice SQLite/FTS5, búsqueda, cachés |
| 09 | [Estrategia de pruebas](09-estrategia-de-pruebas.md) | Pirámide, herramientas, contratos, simulación multi-dispositivo, umbrales |
| 10 | [Plan de implementación](10-plan-de-implementacion.md) | Fases, tareas ordenadas, criterios de aceptación por tarea |
| 11 | [Requisitos no funcionales](11-requisitos-no-funcionales.md) | Rendimiento, fiabilidad, seguridad, accesibilidad, i18n, observabilidad |
| 12 | [Plataformas, build y CI/CD](12-plataformas-build-ci.md) | Targets, empaquetado, firma, pipelines de GitHub Actions |
| 13 | [Convenciones de código](13-convenciones-de-codigo.md) | Estilo, nombres, errores, async, logging, analizadores |
| ADR | [Decisiones de arquitectura](adr/) | Registros de decisiones (ADR) con su justificación |

## Principios rectores

1. **Los ficheros del usuario son la fuente de verdad.** Un vault es una carpeta de ficheros Markdown legibles por cualquier otra herramienta. Basalt nunca encierra datos en un formato propietario.
2. **Nunca perder datos.** Ante cualquier duda (conflicto, error, corte de red, cierre inesperado) se conserva una copia. Esto prevalece sobre la comodidad.
3. **Dominio puro, infraestructura intercambiable.** Toda la lógica de negocio (enlaces, búsqueda, planificación de sincronización, fusión de texto) vive en código sin dependencias de E/S y se prueba de forma exhaustiva y determinista.
4. **Sincronización independiente por vault.** Cada vault tiene su propio repositorio, rama, cuenta, política y estado. Ningún fallo de un vault afecta a otro.
5. **Test-first.** Ninguna funcionalidad se considera hecha sin pruebas unitarias y de integración que la cubran, con umbrales de cobertura y de mutación verificados en CI.

## Convención de idioma

- Las especificaciones están en español.
- **El código (identificadores, comentarios, mensajes de commit, nombres de tests) se escribe en inglés.**
- Los textos visibles al usuario se localizan mediante recursos (`.resx`), con `en` y `es` como idiomas iniciales.

## Palabras normativas

Se usan con el sentido de RFC 2119: **DEBE** (obligatorio), **NO DEBE** (prohibido), **DEBERÍA** (recomendado salvo razón justificada), **PUEDE** (opcional).
