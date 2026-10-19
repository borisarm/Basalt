# 11 — Requisitos no funcionales

Cada requisito `NFR-XXX` con métrica verificable DEBE tener un test automatizado (benchmark o test de integración) o, si no es automatizable, un ítem en la lista de verificación manual de release (`docs/release-checklist.md`, que el agente crea en la Fase 11).

**Equipo de referencia**: escritorio — CPU de 4 núcleos ~2020, 16 GB RAM, SSD. iOS — iPhone 12 / iPad de 9.ª gen.

**Vault de referencia**: generado por `tools/Basalt.VaultGenerator` (determinista con semilla): 10 000 notas, media 2 KB (máx. 200 KB), 5 enlaces y 2 etiquetas por nota de media, 1 000 adjuntos (imágenes 50–500 KB), 300 carpetas en 4 niveles.

## Rendimiento

| ID | Requisito | Objetivo (escritorio / iOS) | Verificación |
|----|-----------|------------------------------|--------------|
| NFR-001 | Arranque en frío hasta lista de vaults | < 1.5 s / < 2 s | Manual + log de tiempos |
| NFR-002 | Abrir vault de referencia con índice existente hasta poder editar | < 2 s / < 3 s | Test de integración `[Trait("Category","Performance")]` |
| NFR-003 | Indexado completo inicial del vault de referencia | < 60 s / < 180 s, sin bloquear UI | Benchmark |
| NFR-004 | Primeros resultados de búsqueda | < 300 ms | Benchmark (BenchmarkDotNet en `tests/Basalt.Benchmarks`) |
| NFR-005 | Quick switcher (cada pulsación) | < 50 ms | Benchmark sobre `FuzzyMatcher` con 10 000 títulos |
| NFR-006 | Latencia de tecleo en editor (nota 100 KB) | < 16 ms p95 | Manual + medición en modo diagnóstico |
| NFR-007 | Actualización de backlinks tras guardar | < 500 ms | Test de integración |
| NFR-008 | Renombrar nota con 500 backlinks | < 3 s | Test de integración |
| NFR-009 | Ciclo de sync sin cambios (304 en ref) | < 1 s (con hashcache válida) | Test de integración contra FakeGitHub |
| NFR-010 | Escaneo local de sync del vault de referencia con hashcache válida | < 2 s | Benchmark |
| NFR-011 | Clonado del vault de referencia (zipball) con red de 50 Mbps | < 60 s | Test de integración (FakeGitHub con limitación de ancho de banda) |
| NFR-012 | Grafo global 5 000 nodos | ≥ 30 fps / ≥ 20 fps | Manual |
| NFR-013 | Memoria con vault de referencia abierto | < 500 MB / < 300 MB | Manual + test de humo |

## Fiabilidad y datos

- NFR-020. **Cero pérdida de datos**: la batería de simulación multi-dispositivo ([09](09-estrategia-de-pruebas.md) §7) con ≥ 1 000 escenarios en cada PR y ≥ 10 000 en la ejecución nocturna no produce ninguna pérdida ni divergencia.
- NFR-021. Escrituras de ficheros atómicas (temp + rename), verificadas por tests que interrumpen la escritura (inyección de fallos en `IFileWriter`).
- NFR-022. Cierre forzado en cualquier punto de un ciclo de sync no corrompe el vault ni el estado (tests de "crash" que cancelan en cada uno de los pasos y reanudan).
- NFR-023. La app nunca modifica ficheros fuera de la carpeta del vault y de `{AppData}`.
- NFR-024. Las ediciones del editor se guardan como máximo 2 s después de la última pulsación y al cerrar.

## Seguridad

- NFR-030. Tokens solo en almacén seguro del SO; test que busca el token de prueba en todos los ficheros de `{AppData}` y logs tras un ciclo completo y falla si lo encuentra.
- NFR-031. Sin ejecución de contenido de notas (no hay JavaScript; el HTML en Markdown se muestra como texto/escapado en modo lectura).
- NFR-032. Regex de usuario con `RegexOptions.NonBacktracking` y timeout.
- NFR-033. YAML analizado sin resolución de tipos arbitrarios; límites de profundidad y tamaño (frontmatter > 64 KB → se trata como inválido).
- NFR-034. Dependencias revisadas con `dotnet list package --vulnerable` en CI (falla con vulnerabilidades altas/críticas).
- NFR-035. Zip (zipball) extraído con protección contra *zip slip* (rutas validadas con `VaultPath`) y límite de tamaño descomprimido.

## Usabilidad y accesibilidad

- NFR-040. Navegación completa por teclado en escritorio (todas las acciones de F-001…F-015 alcanzables sin ratón).
- NFR-041. Lectores de pantalla: Narrator (Windows) y VoiceOver (macOS/iOS) leen nombres de controles, estado de sync y notificaciones.
- NFR-042. Contraste WCAG 2.1 AA en ambos temas (test automatizado sobre los tokens de color: ratio ≥ 4.5:1 texto normal).
- NFR-043. Objetivos táctiles ≥ 44×44 pt en iOS.
- NFR-044. Localización completa en `en` y `es`; test que verifica que todas las claves existen en ambos `.resx` y que ningún texto visible está codificado en XAML (analizador / test sobre los `.axaml`).

## Observabilidad

- NFR-050. Logging estructurado con `ILogger<T>` y `LoggerMessage` source-generated (alto rendimiento). Niveles: `Information` para eventos de ciclo de vida (vault abierto, sync completada con contadores), `Warning` para incidencias recuperables, `Error` para fallos, `Debug` para detalle (rutas).
- NFR-051. Cada ciclo de sync tiene un `SyncRunId` (correlación) presente en todos sus logs.
- NFR-052. "Copiar información de diagnóstico" en Acerca de: versión, SO, vaults (sin nombres de ficheros), estado de sync, últimas 200 líneas de log redactadas.
- NFR-053. Sin telemetría remota.

## Mantenibilidad

- NFR-060. Umbrales de calidad de [09](09-estrategia-de-pruebas.md) §10 aplicados en CI.
- NFR-061. Complejidad ciclomática por método ≤ 15 (analizador; excepciones justificadas con `[SuppressMessage]` y motivo).
- NFR-062. Sin advertencias de compilación (`TreatWarningsAsErrors=true`).
- NFR-063. Trimming/AOT: el head iOS compila con trimming completo sin advertencias de trimming de código propio (`IsTrimmable=true` en proyectos del núcleo).
