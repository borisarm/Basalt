# 0011 — Almacenes JSON: `IDocumentStore<T>` como puerto

- Estado: Aceptado
- Fecha: 2026-10-20

## Contexto
03 §3 lista `I*Store (JSON) | JsonFileStore<T> | InMemoryStore<T> | KeyValueStoreContract`. Implementar cada almacén (`IVaultRegistry`, `IAccountStore`, `IAppSettingsStore`) en el adaptador duplicaría su lógica (altas, bajas, unicidad) sin poder probarla con el fake.

## Decisión
- El puerto de salida es `IDocumentStore<T>` (fake `InMemoryStore<T>`, real `JsonFileStore<T>`, contrato `KeyValueStoreContract<T>`).
- `VaultRegistry`, `AccountStore` y `AppSettingsStore` son servicios de Aplicación sobre ese puerto; sus interfaces mantienen los nombres de 03 §1.6.
- `IVaultConfigStore` (ficheros dentro del vault) es un puerto propio sobre `IVaultFileSystem`.
- Lectura tolerante: documento ilegible o de versión más nueva → valor por defecto (`Invalid`); antes de la primera escritura se guarda una copia `*.corrupt-{timestamp}` (nunca perder datos).

## Consecuencias
- ✅ La lógica de los almacenes se prueba una vez contra el fake; el adaptador solo serializa.
- ⚠️ Los ajustes ausentes de `vault.json` se rellenan con los valores por defecto al leer.
