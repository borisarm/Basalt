# 13 — Convenciones de código

## 1. Estilo general

- C# moderno: `file-scoped namespaces`, `primary constructors` en servicios (con campos `readonly` explícitos si se reasignan o validan), `record`/`record struct` para datos inmutables, `required` en DTOs, *collection expressions*, *pattern matching*.
- Un tipo público por fichero; nombre de fichero = nombre del tipo.
- Namespaces = estructura de carpetas (`Basalt.Domain.Links`, `Basalt.Application.Sync`…).
- `sealed` por defecto en clases no diseñadas para herencia.
- `internal` por defecto en adaptadores; `public` solo lo que el contrato exige.
- Sin regiones (`#region`).
- `.editorconfig` en la raíz con las reglas `IDE*` aplicadas como `warning` (por tanto error en build): `var` cuando el tipo es evidente, llaves obligatorias, `this.` no usado, campos privados `_camelCase`, constantes `PascalCase`, interfaces `I*`, async con sufijo `Async`.

## 2. Nombres

| Elemento | Convención | Ejemplo |
|----------|-----------|---------|
| Comando de caso de uso | Verbo imperativo + sustantivo | `RenameEntry`, `SyncVault` |
| Handler | `{Comando}Handler` | `RenameEntryHandler` |
| Evento | Participio pasado | `NoteSaved`, `SyncCompleted` |
| Puerto de salida | `I{Capacidad}` | `IRemoteRepository`, `ISecretStore` |
| Adaptador | `{Tecnología}{Capacidad}` | `SqliteNoteIndex`, `GitHubRemoteRepository` |
| Fake de tests | `InMemory{X}` / `Fake{X}` | `InMemoryVaultFileSystem`, `FakeClock` |
| ViewModel | `{Vista}ViewModel` | `BacklinksViewModel` |
| Test class | `{ClaseBajoPrueba}Tests` | `SyncPlannerTests` |
| Test method | `Method_Scenario_ExpectedResult` | `Plan_BothSidesModifiedText_ProducesMergeAction` |
| Código de error | `modulo.snake_case` | `path.case_collision` |

## 3. Errores

- Catálogo único en `Basalt.Application/Errors/ErrorCodes.cs` como constantes `string`; los del dominio en `Basalt.Domain/Common/DomainErrors.cs`. Ningún código de error literal fuera de estos ficheros.
- Test que verifica que todo código de error tiene traducción en `en` y `es` (`Errors.{code}`).
- No usar excepciones para flujo de control. `ArgumentException`/`ArgumentNullException.ThrowIfNull` solo para violaciones de contrato de programación.
- Los adaptadores capturan excepciones específicas (`IOException`, `UnauthorizedAccessException`, `HttpRequestException`, `TaskCanceledException` por timeout, `SqliteException`) y devuelven `Error`; nunca `catch (Exception)` salvo en la frontera más externa (manejador global) con log `Critical`.
- `OperationCanceledException` provocada por el `ct` del llamador se propaga (no se convierte en `Error`).

## 4. Asíncrono

- `async`/`await` de punta a punta; `ConfigureAwait(false)` en Domain/Application/Adapters (no en Presentation/UI).
- `ValueTask` solo en caminos calientes con resultado síncrono frecuente (p. ej. caché).
- Prohibido `async void` salvo manejadores de eventos de UI (y estos delegan inmediatamente en un comando que captura excepciones).
- Todo método que haga E/S acepta `CancellationToken` y lo propaga.
- Concurrencia con `SemaphoreSlim`, `Channel<T>` y estructuras inmutables; no `lock` sobre objetos públicos.

## 5. Inyección de dependencias

- Solo inyección por constructor. Nada de `IServiceProvider` fuera de `Basalt.Composition` y de las factorías de scope (`IVaultScopeFactory`).
- Cada proyecto expone `Add{Proyecto}(this IServiceCollection)`.
- Opciones con `IOptions<T>` solo en adaptadores (p. ej. `GitHubOptions { ClientId, ApiBaseUrl }`); el núcleo recibe valores de dominio.

## 6. Logging

- `ILogger<T>` inyectado; mensajes con `[LoggerMessage]` source-generated en una clase estática `Log` parcial por componente.
- Nunca registrar: tokens, contenido de notas, cabeceras `Authorization`. Rutas solo en `Debug`.
- Ámbitos (`BeginScope`) con `VaultId` y `SyncRunId`.

## 7. Documentación en código

- Comentarios XML (`///`) obligatorios en: todos los puertos (interfaces) públicos, comandos/queries de casos de uso, y servicios de dominio públicos — describiendo contrato, errores posibles (`<returns>` con códigos) y concurrencia.
- Los comentarios explican el **porqué**, no el qué. Referenciar la especificación cuando una regla venga de ella: `// Spec 06 §5 row 10`.

## 8. Tests (ver también 09)

- Estructura *Arrange / Act / Assert* separada por líneas en blanco (sin comentarios `// Arrange`).
- Una razón de fallo por test (múltiples aserciones permitidas si verifican un mismo comportamiento; usar `using (new AssertionScope())`).
- Builders fluidos para datos: `A.Note().At("x/y.md").WithContent("...").Build()`, `A.Manifest().With("a.md", "content").Build()`.
- Prohibido `Thread.Sleep`/`Task.Delay` con tiempo real en tests unitarios: usar `FakeClock`/`FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`). Los componentes temporizados (debounce, intervalos, backoff) reciben `TimeProvider`.
- Tests de integración con directorios temporales únicos (`TempDirectory` fixture con `IAsyncDisposable` que limpia).

## 9. Commits y ramas (del propio proyecto Basalt)

- Conventional Commits: `feat(sync): …`, `fix(links): …`, `test(domain): …`, `refactor`, `docs`, `chore`, `build`, `ci`.
- Un commit por tarea del plan (o varios pequeños coherentes), siempre en verde.
