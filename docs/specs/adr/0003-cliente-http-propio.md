# 0003 — Cliente HTTP propio en lugar de Octokit

- Estado: Aceptado
- Fecha: 2026-10-19

## Contexto
Se necesitan ~20 endpoints de la API de GitHub. Octokit.NET cubre la API completa pero usa serialización basada en reflexión y abstrae detalles que necesitamos controlar (ETag/304, `retry-after`, contenido raw de blobs, streaming de zipball).

## Decisión
Cliente propio mínimo (`GitHubApiClient`) sobre `HttpClient` + `IHttpClientFactory` + `Microsoft.Extensions.Http.Resilience`, con DTOs y `JsonSerializerContext` generados.

## Consecuencias
- ✅ Compatible con trimming/AOT en iOS.
- ✅ Control fino de reintentos, límites de tasa y peticiones condicionales.
- ✅ Superficie pequeña, totalmente cubierta por tests contra FakeGitHub.
- ⚠️ Mantenimiento propio de los DTOs (acotado a los endpoints de 06 §9.3).
