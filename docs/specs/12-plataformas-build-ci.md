# 12 — Plataformas, build y CI/CD

## 1. Targets

| Proyecto | TFM | Notas |
|----------|-----|-------|
| Núcleo, adaptadores portables, Presentation, UI, Composition | `net10.0` | `IsTrimmable=true`, `IsAotCompatible=true` en Domain/Application |
| `Basalt.Adapters.Platform.Windows` | `net10.0-windows` | P/Invoke a `advapi32` (Credential Manager) con `LibraryImport` |
| `Basalt.Adapters.Platform.Apple` | `net10.0` (macOS vía P/Invoke a `Security.framework`) y `net10.0-ios` | Multi-target; misma API Keychain |
| `Basalt.Desktop` | `net10.0` (macOS) y `net10.0-windows` (Windows) | Multi-target condicionado por SO de build |
| `Basalt.iOS` | `net10.0-ios` | Mínimo iOS 16 |

Requisitos mínimos de SO: Windows 10 1809+ (x64, arm64), macOS 13+ (arm64, x64), iOS 16+.

## 2. Ficheros raíz

**`global.json`**
```json
{ "sdk": { "version": "11.0.100-rc.2.26504.105", "rollForward": "latestFeature", "allowPrerelease": true },
  "test": { "runner": "Microsoft.Testing.Platform" } }   // ver ADR-0008
```

**`Directory.Build.props`** (extracto normativo)
```xml
<Project>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);CS1591</NoWarn>   <!-- docs XML solo obligatorias en puertos públicos (ver 13) -->
    <InvariantGlobalization>false</InvariantGlobalization>
    <Deterministic>true</Deterministic>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup Condition="'$(IsTestProject)' != 'true'">
    <PackageReference Include="Microsoft.CodeAnalysis.BannedApiAnalyzers" PrivateAssets="all" />
    <AdditionalFiles Include="$(MSBuildThisFileDirectory)BannedSymbols.txt" Condition="'$(BasaltCore)' == 'true'" />
  </ItemGroup>
</Project>
```
`Basalt.Domain` y `Basalt.Application` establecen `<BasaltCore>true</BasaltCore>`. `BannedSymbols.txt` prohíbe: `System.DateTime.Now`, `System.DateTime.UtcNow`, `System.DateTimeOffset.Now`, `System.DateTimeOffset.UtcNow`, `System.Guid.NewGuid`, `System.Random`, `System.IO.File`, `System.IO.Directory`, `System.IO.FileStream`, `System.Environment`, `System.Threading.Thread.Sleep`, `System.Threading.Tasks.Task.Wait`, `System.Threading.Tasks.Task\`1.Result`, `System.Console`.

`tests/Directory.Build.props` habilita Microsoft.Testing.Platform (`<UseMicrosoftTestingPlatformRunner>`/`TestingPlatformDotnetTestSupport` según la versión del SDK) y referencias comunes de tests.

## 3. Empaquetado

| Plataforma | Formato | Herramienta |
|------------|---------|-------------|
| Windows | MSIX (firmado) + ZIP portable | `dotnet publish -r win-x64 / win-arm64 --self-contained -p:PublishReadyToRun=true`; MSIX vía `MakeAppx` o Windows App SDK packaging project |
| macOS | `.app` universal (arm64 + x64 con `lipo`) en `.dmg`, firmado y notarizado | `dotnet publish -r osx-arm64/osx-x64`, script `build/macos/bundle.sh` (Info.plist, iconos, entitlements, `codesign`, `notarytool`) |
| iOS | `.ipa` para TestFlight/App Store | `dotnet publish -f net10.0-ios -c Release` con perfil de aprovisionamiento |

### iOS Info.plist (claves requeridas)
- `UIFileSharingEnabled = true`, `LSSupportsOpeningDocumentsInPlace = true` (vaults visibles en Archivos).
- `BGTaskSchedulerPermittedIdentifiers = [net.basalt.sync.refresh]`, `UIBackgroundModes = [fetch, processing]`.
- `ITSAppUsesNonExemptEncryption = false` (solo HTTPS estándar).

### macOS
- Entitlements: `com.apple.security.network.client`; si se distribuye fuera de la App Store, sin sandbox (para acceder a carpetas arbitrarias). Con App Store: sandbox + *security-scoped bookmarks* para carpetas de vaults (abstraído en `IFolderAccessGrantStore` del adaptador de plataforma).
- Keychain: grupo de acceso propio.

## 4. Integración continua (GitHub Actions)

`.github/workflows/ci.yml` — en cada push y PR:

| Job | Runner | Pasos |
|-----|--------|-------|
| `build-test-core` | `ubuntu-latest`, `windows-latest`, `macos-latest` (matriz) | restore → build (Release, warnings as errors) → tests de todos los proyectos salvo UI y Live → cobertura (Cobertura XML) → publicar resultados |
| `ui-tests` | `windows-latest`, `macos-latest` | `Basalt.UI.Tests` (Avalonia.Headless) |
| `sync-simulation` | `ubuntu-latest` | `Basalt.Sync.IntegrationTests` con `BASALT_SIM_SCENARIOS=1000` |
| `coverage-gate` | `ubuntu-latest` | ReportGenerator; falla si no se cumplen umbrales de [09](09-estrategia-de-pruebas.md) §10 |
| `architecture` | `ubuntu-latest` | `Basalt.Architecture.Tests` + test de trazabilidad de specs |
| `build-ios` | `macos-latest` | `dotnet workload restore`; build `net10.0-ios` (simulador, sin firma) |
| `security` | `ubuntu-latest` | `dotnet list package --vulnerable --include-transitive` |
| `format` | `ubuntu-latest` | `dotnet format --verify-no-changes` |

`.github/workflows/nightly.yml` — diario:
- Mutación con Stryker sobre `Basalt.Domain` y `Basalt.Application` (umbral de ruptura de [09](09-estrategia-de-pruebas.md) §10).
- Simulación de sync con `BASALT_SIM_SCENARIOS=10000` y semillas aleatorias (la semilla fallida se publica en el resumen para reproducir).
- `Basalt.Adapters.GitHub.LiveTests` si existen los secretos `BASALT_LIVE_GITHUB_TOKEN` y `BASALT_LIVE_GITHUB_OWNER` (crea un repo temporal `basalt-live-test-{runId}`, ejecuta el contrato, lo borra).
- Benchmarks con comparación contra la línea base (aviso si regresión > 20 %).

`.github/workflows/release.yml` — en tags `v*`: empaquetado y firma de las tres plataformas (secretos de firma), artefactos adjuntos a la release, subida a TestFlight.

## 5. Comandos locales (documentados en CLAUDE.md)

```bash
dotnet build Basalt.slnx -c Debug
dotnet test --solution Basalt.slnx                     # todos los tests (salvo Live)
dotnet test --project tests/Basalt.Domain.Tests
dotnet test --solution Basalt.slnx -- --filter-trait "Spec=F-004.4"  # sintaxis exacta según runner; ver CLAUDE.md
dotnet run --project src/Basalt.Desktop
dotnet tool run dotnet-stryker -p src/Basalt.Domain/Basalt.Domain.csproj
dotnet format
```
