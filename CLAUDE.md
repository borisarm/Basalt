# CLAUDE.md — Basalt

Basalt es un clon de Obsidian para Windows, macOS e iOS en **.NET 10 + Avalonia**, con sincronización por vault sobre **repositorios de GitHub (un repo por vault)**. Arquitectura **monolito hexagonal**, principios **SOLID**, desarrollo **TDD**.

La especificación completa está en `docs/specs/`. **La especificación manda**: si el código y la especificación discrepan, se corrige el código; si la especificación es ambigua o incompleta, se toma la decisión más conservadora (la que nunca pierde datos), se documenta en un ADR (`docs/specs/adr/NNNN-titulo.md`) y se continúa.

## Cómo trabajar

1. Lee `PROGRESS.md` y toma la **primera tarea no completada** de `docs/specs/10-plan-de-implementacion.md`. No saltes tareas salvo que estén bloqueadas (en ese caso anótalo en `PROGRESS.md`).
2. Lee las secciones de la especificación que la tarea referencia antes de escribir código.
3. **TDD estricto** (`docs/specs/09-estrategia-de-pruebas.md` §1):
   - Escribe los tests primero, ejecútalos y comprueba que fallan por la razón correcta.
   - Implementa lo mínimo para pasar; refactoriza en verde.
   - Etiqueta cada test que cubre un escenario o regla con `[Trait("Spec", "<id>")]`.
   - Unitarios **y** de integración: si la tarea toca un adaptador o cruza capas, añade tests de integración con recursos reales (disco temporal, SQLite, FakeGitHub por HTTP).
4. Cumple la **Definition of Done** del plan antes de marcar la tarea: build sin advertencias, todos los tests en verde, formato, cobertura (y mutación si tocaste el dominio), arquitectura, `PROGRESS.md`, commit.
5. Haz un commit por tarea con Conventional Commits (`feat(links): implement LinkResolver (T1.4)`).

## Comandos

```bash
dotnet build Basalt.slnx
dotnet test --solution Basalt.slnx
dotnet test --project tests/Basalt.Domain.Tests
dotnet test --project tests/Basalt.Domain.Tests -- --filter-trait "Spec=SYNC-ROW-10"
dotnet test --solution Basalt.slnx --coverage --coverage-output-format cobertura
dotnet tool run reportgenerator -reports:**/*.cobertura.xml -targetdir:artifacts/coverage -reporttypes:TextSummary
dotnet tool run dotnet-stryker   # desde tests/Basalt.Domain.Tests (stryker-config.json, runner mtp)
bash build/cover.sh tests/Basalt.Domain.Tests   # cobertura + puerta de umbrales
dotnet format --verify-no-changes
dotnet run --project src/Basalt.Desktop -- --appdata <carpeta>   # datos de la app en otra carpeta (portable/pruebas)
dotnet run --project tools/Basalt.VaultGenerator -- <carpeta> [--notes N]   # vault de referencia determinista
```

(Si la sintaxis exacta de `dotnet test`/filtros cambia con la versión del SDK o del runner, actualiza esta sección.)

## Reglas no negociables

- **Nunca perder datos del usuario.** Ante la duda: conservar ambas versiones.
- `Basalt.Domain` es puro: sin E/S, sin reloj, sin `Guid.NewGuid`, sin `Random`, sin paquetes NuGet. `Basalt.Application` solo conoce puertos. Lo vigilan `BannedSymbols.txt` y `Basalt.Architecture.Tests`: no los desactives ni los relajes.
- `Basalt.Presentation` no referencia Avalonia.
- Tipos de adaptadores `internal`; solo se exponen las extensiones de registro DI.
- Errores esperados con `Result<T>` y códigos del catálogo (`ErrorCodes`), no excepciones.
- Todo puerto de salida tiene: fake en `Basalt.Testing`, adaptador real y clase de **tests de contrato** que ambos pasan.
- Nada de red real en tests salvo `Basalt.Adapters.GitHub.LiveTests`. Usa `Basalt.Testing.FakeGitHub`.
- Nada de esperas con tiempo real en tests unitarios: `FakeTimeProvider`.
- Nunca registres tokens ni contenido de notas.
- No bajes umbrales de cobertura/mutación ni marques tests como `Skip` para pasar CI.
- No cambies el comportamiento especificado sin actualizar la especificación en el mismo commit.
- Código, identificadores, comentarios y nombres de tests en **inglés**; textos de UI en `.resx` (`en`, `es`).

## Mapa de la especificación

| Necesito… | Leer |
|-----------|------|
| Alcance y glosario | `docs/specs/00-vision-y-alcance.md` |
| Proyectos, dependencias, patrones | `docs/specs/01-arquitectura.md` |
| Tipos del dominio e invariantes | `docs/specs/02-modelo-de-dominio.md` |
| Firmas de puertos y casos de uso | `docs/specs/03-puertos-y-adaptadores.md` |
| Comportamiento visible (Gherkin) | `docs/specs/04-especificacion-funcional.md` |
| Reglas de Markdown/enlaces | `docs/specs/05-markdown-y-enlaces.md` |
| Sincronización con GitHub | `docs/specs/06-sincronizacion-github.md` |
| Vistas y ViewModels | `docs/specs/07-interfaz-usuario.md` |
| Índice, búsqueda, ficheros de config | `docs/specs/08-persistencia-e-indexacion.md` |
| Cómo probar | `docs/specs/09-estrategia-de-pruebas.md` |
| Qué hacer ahora | `docs/specs/10-plan-de-implementacion.md` + `PROGRESS.md` |
| Métricas | `docs/specs/11-requisitos-no-funcionales.md` |
| Build/CI/empaquetado | `docs/specs/12-plataformas-build-ci.md` |
| Estilo | `docs/specs/13-convenciones-de-codigo.md` |
