# 0012 — Sesiones de vault explícitas en lugar de scopes de DI

- Estado: Aceptado
- Fecha: 2026-10-20

## Contexto
01 §4.5 propone un scope de DI por vault (`IVaultScopeFactory`). Abrir un vault es asíncrono (sistema de ficheros, `vault.json`, índice) y puede fallar con errores esperados; los handlers son transitorios y no deben ver `IServiceProvider` (13 §5).

## Decisión
- `VaultSession` (Aplicación) agrupa los servicios de un vault abierto: sistema de ficheros, ajustes, `AsyncReaderWriterLock` y los recursos por vault que se añaden en tareas posteriores (`Own`), que se liberan al cerrar.
- `IVaultSessions` es el registro de sesiones abiertas (singleton). Los handlers obtienen la sesión con `Get(VaultId)`.
- Las sesiones las construye el caso de uso `OpenVault` con factorías de puertos (`IVaultFileSystemFactory`, factoría de índice…), sin scopes de DI.

## Consecuencias
- ✅ Apertura asíncrona con `Result<T>`; sin Service Locator fuera de la composición.
- ✅ Aislamiento entre vaults igual que con scopes: cada sesión tiene sus propias instancias.
- ⚠️ Los servicios por vault se crean con factorías explícitas en lugar de registrarse como `Scoped`.
