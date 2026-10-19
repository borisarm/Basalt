# 0001 — Stack tecnológico y versiones

- Estado: Aceptado (las versiones exactas las fija el agente en la tarea T0.1)
- Fecha: 2026-10-19

## Contexto
Se requiere una única base de código para Windows, macOS e iOS con UI nativa de buen rendimiento y un núcleo fuertemente probado.

## Decisión
- **.NET 10** (LTS) con C# más reciente; SDK fijado en `global.json`.
- **Avalonia UI** (última versión estable compatible con `net10.0-ios` al inicio del proyecto) con tema Fluent; **AvaloniaEdit** + TextMate para el editor.
- **CommunityToolkit.Mvvm** para MVVM.
- **Markdig** + **YamlDotNet** para Markdown/frontmatter (solo en el adaptador).
- **SQLite** (`Microsoft.Data.Sqlite` + `e_sqlite3` con FTS5) para el índice.
- **System.Text.Json** con source generators (trimming/AOT en iOS).
- Tests: **xUnit v3**, **AwesomeAssertions**, **NSubstitute**, **FsCheck**, **Verify**, **Bogus**, **Avalonia.Headless**, **ArchUnitNET**, **Stryker.NET**.
- Central Package Management (`Directory.Packages.props`).

## Consecuencias
- iOS exige trimming: se evita reflexión en serialización y DI se registra explícitamente.
- Se descartan librerías con licencias comerciales recientes (FluentAssertions ≥ 8, MediatR ≥ 13, AutoMapper ≥ 15).

## Versiones fijadas (T0.1, 2026-10-19)

| Paquete / herramienta | Versión |
|-----------------------|---------|
| SDK .NET | 11.0.100-rc.2 (ver ADR-0008); TFM `net10.0`, runtime 10.0.13 |
| Avalonia (+ Desktop, iOS, Themes.Fluent, Fonts.Inter, Headless, Headless.XUnit) | 12.1.4 |
| Avalonia.AvaloniaEdit / AvaloniaEdit.TextMate | 12.0.0 |
| CommunityToolkit.Mvvm | 8.4.2 |
| Microsoft.Extensions.* (DI, Hosting, Logging, Options, Http) | 10.0.13 |
| Microsoft.Extensions.Http.Resilience | 10.11.0 |
| Microsoft.Extensions.TimeProvider.Testing | 10.11.0 |
| Serilog.Extensions.Logging / Serilog.Sinks.File | 10.0.0 / 7.0.0 |
| Markdig | 1.4.2 |
| YamlDotNet | 18.1.0 |
| Microsoft.Data.Sqlite / SQLitePCLRaw.bundle_e_sqlite3 | 10.0.13 / 3.0.5 |
| xunit.v3 (Microsoft.Testing.Platform v2) | 4.0.2 |
| AwesomeAssertions | 9.6.0 |
| NSubstitute | 6.2.0 |
| FsCheck / FsCheck.Xunit.v3 | 3.5.0 |
| Bogus | 35.6.5 |
| Microsoft.Testing.Extensions.CodeCoverage | 18.12.0 |
| TngTech.ArchUnitNET (+ xUnitV3) | 0.13.4 |
| Microsoft.AspNetCore.TestHost | 10.0.13 |
| BenchmarkDotNet | 0.15.8 |
| Microsoft.CodeAnalysis.BannedApiAnalyzers | 5.6.0 |
| dotnet-stryker / reportgenerator | 5.0.0 / 5.5.11 |

Verify.XunitV3 se descartó (ver ADR-0009).

## Alternativas consideradas
- .NET MAUI: menos control del renderizado y peor soporte de escritorio macOS (Mac Catalyst).
- Uno Platform: viable, pero Avalonia tiene mejor ecosistema de editor de texto (AvaloniaEdit).
