# Registros de decisiones de arquitectura (ADR)

Formato de cada ADR (`NNNN-titulo-en-kebab-case.md`):

```markdown
# NNNN — Título
- Estado: Propuesto | Aceptado | Sustituido por NNNN
- Fecha: YYYY-MM-DD
## Contexto
## Decisión
## Consecuencias
## Alternativas consideradas
```

| ADR | Título | Estado |
|-----|--------|--------|
| [0001](0001-stack.md) | Stack tecnológico y versiones | Aceptado |
| [0002](0002-sync-api-github-sin-git-local.md) | Sincronización vía API Git Data de GitHub, sin Git local | Aceptado |
| [0003](0003-cliente-http-propio.md) | Cliente HTTP propio en lugar de Octokit | Aceptado |
| [0004](0004-monolito-hexagonal.md) | Monolito hexagonal modular | Aceptado |
| [0005](0005-cifrado-e2e-diferido.md) | Cifrado de extremo a extremo diferido | Aceptado |
| [0006](0006-editor-ios.md) | Editor en iOS | Propuesto (validación en simulador pendiente) |
| [0007](0007-ui-tests.md) | Tests de UI con Avalonia.Headless + xUnit v3 | Aceptado |
| [0008](0008-sdk-11-con-tfm-net10.md) | SDK .NET 11 con TFM `net10.0` | Aceptado |
| [0009](0009-snapshots-propios-sin-verify.md) | Snapshots propios en lugar de Verify | Aceptado |
| [0010](0010-modulo-metadata.md) | Módulo de dominio `Metadata` para `NoteMetadata` | Aceptado |
| [0011](0011-almacenes-de-documentos.md) | Almacenes JSON con `IDocumentStore<T>` | Aceptado |
| [0012](0012-sesiones-de-vault.md) | Sesiones de vault explícitas en lugar de scopes de DI | Aceptado |
| [0013](0013-busqueda-por-subcadena-con-trigramas.md) | Búsqueda por subcadena: FTS5 `trigram` sobre texto plegado | Aceptado |
| [0014](0014-json-invalido-se-aparta.md) | Un JSON compartido inválido se aparta antes de sobrescribirlo | Aceptado |
| [0015](0015-registro-de-casos-de-uso-por-reflexion.md) | Registro de casos de uso por reflexión en la composición | Aceptado |
