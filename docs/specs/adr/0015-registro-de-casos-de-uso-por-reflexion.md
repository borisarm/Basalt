# 0015 — Registro de casos de uso por reflexión en la composición

- Estado: Aceptado
- Fecha: 2026-10-20

## Contexto
`Basalt.Application` tiene más de 40 casos de uso, y cada comando necesita la cadena de decoradores logging → validación → bloqueo por vault (01 §4.1). Registrar cada uno a mano en `Basalt.Composition` duplica la lista de handlers, y es fácil olvidarse de alguno. `Basalt.Application` no depende de DI (solo conoce puertos).

## Decisión
- `Basalt.Composition` descubre por reflexión los tipos concretos de `Basalt.Application` que implementan `ICommandHandler<,>` o `IQueryHandler<,>` (`CompositionServiceCollectionExtensions.UseCases`).
- Los registra como transitorios; los comandos se envuelven con `ActivatorUtilities` en `LoggingHandlerDecorator(ValidationHandlerDecorator(VaultLockDecorator(handler)))`.
- Las consultas no llevan decoradores.
- Un test de arquitectura construye el contenedor con `ValidateOnBuild` y resuelve todos los casos de uso, los puertos, el shell, el workspace y los ajustes (09 §8).

## Consecuencias
- ✅ Un caso de uso nuevo queda registrado y decorado sin tocar la composición.
- ⚠️ La reflexión y `MakeGenericType` no son seguros con recorte o AOT. En iOS (fase 10), antes de activar el recorte, hay que preservar `Basalt.Application` (`TrimmerRootAssembly`) o generar el registro con un generador de código.
