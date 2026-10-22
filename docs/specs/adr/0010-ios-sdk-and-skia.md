# ADR-0010 — SDK y SkiaSharp para el head iOS

- Estado: aceptado (provisional hasta que el workload de .NET 11 y Xcode estén alineados)
- Fecha: 2026-10-22
- Contexto: T10.1, spec [12 §3](../12-plataformas-build-ci.md)

## Contexto

`global.json` fija el SDK 11.0.100-rc.2 y el TFM del núcleo es `net10.0`. Al crear `Basalt.iOS` y compilarlo en macOS:

- Con el SDK 11 RC2, el workload `ios` instala el pack `Microsoft.iOS.Sdk.net10.0_26.0` (requiere Xcode 26.0; el Mac tiene Xcode 26.5). Además, NuGet resuelve `SkiaSharp` 3.119.4 a su asset `lib/net10.0`, porque el TPV del proyecto es 26.0 y SkiaSharp solo publica `net10.0-ios26.2`.
- Ese asset `net10.0` no incluye el resolutor de P/Invoke de iOS: la app arranca, carga `libSkiaSharp.framework` por `@rpath`, pero `DllImport("libSkiaSharp")` lanza `DllNotFoundException` en el simulador.
- Con el SDK 10.0.201 del sistema (workload iOS 26.4) el proyecto resuelve `SkiaSharp` a `net10.0-ios26.2` y la app arranca y muestra la pantalla de vaults en el simulador iPhone 17 Pro (iOS 26.5).

## Decisión

1. Añadir `SkiaSharp.NativeAssets.iOS` 3.119.4 como referencia explícita del head iOS, para que el framework nativo viaje en el bundle.
2. Compilar y **ejecutar** el head iOS localmente con el SDK 10 del sistema (workload iOS 26.4) y `-p:ValidateXcodeVersion=false` mientras Xcode 26.5 no tenga un workload de .NET 11 equivalente. Documentar la línea de comandos en `PROGRESS.md`.
3. Mantener `Basalt.iOS` fuera de `Basalt.slnx`: Windows y Linux no pueden compilar `net10.0-ios`. CI lo compila explícitamente en el job `build-ios` (solo build, sin ejecución).
4. En CI se conserva el SDK de `global.json` para el build; cuando el workload de .NET 11 soporte el Xcode del runner, se usará para compilar y ejecutar el head.

## Consecuencias

- T10.1 queda verificado con build + arranque real en simulador, sin depender de macOS para el resto del equipo.
- El SDK usado para iOS difiere del resto de heads hasta que se actualice el workload; el TFM sigue siendo `net10.0-ios` y el código no cambia.
- `ValidateXcodeVersion=false` solo se usa en la verificación local; no se añade al proyecto ni al CI.
