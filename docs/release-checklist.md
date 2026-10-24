# Lista de verificación de release

Proceso para publicar una versión de Basalt. Aplica a las tres plataformas soportadas.

> **Requisito pendiente (T11.4)**: la automatización de empaquetado y firma
> (`.github/workflows/release.yml`, MSIX firmado, `.app` universal notarizada y `.ipa` para
> TestFlight) todavía no está implementada. Hasta entonces, los pasos de empaquetado de este
> documento se ejecutan a mano en un entorno con las herramientas y credenciales indicadas.

## 0. Entorno y secretos

| Secreto / recurso | Uso |
|-------------------|-----|
| Certificado de firma de código Windows (`.pfx`) + contraseña | Firmar el MSIX |
| Apple Developer ID Application (`.p12`) + contraseña | Firmar `Basalt.app` |
| Apple ID / App Store Connect API key (`issuer-id`, `key-id`, `.p8`) | Notarización macOS y subida a TestFlight |
| Perfil de aprovisionamiento iOS + certificado de distribución | Firmar el `.ipa` |
| `BASALT_LIVE_GITHUB_TOKEN`, `BASALT_LIVE_GITHUB_OWNER` | Job nocturno de contrato real (opcional) |

Herramientas locales: .NET SDK fijado en `global.json`, `dotnet workload restore` para iOS,
`signtool`, `makeappx`, `codesign`, `notarytool`, `hdiutil` y Xcode.

## 1. Comprobaciones previas

- [ ] `main` está limpio y sincronizado (`git status`, `git pull`).
- [ ] CI (`ci.yml`) en verde en Windows, macOS y Linux para el commit a publicar.
- [ ] Nightly en verde: mutación, simulación con 10 000 escenarios, benchmarks sin regresión > 20 %
      y `Basalt.Adapters.GitHub.LiveTests` si hay secretos.
- [ ] Sin vulnerabilidades altas/críticas:
      `dotnet list Basalt.slnx package --vulnerable --include-transitive`.
- [ ] `dotnet build Basalt.slnx -c Release` sin advertencias.
- [ ] `dotnet test --solution Basalt.slnx` en verde (los tests Live no se ejecutan en local).
- [ ] `dotnet format Basalt.slnx --verify-no-changes`.
- [ ] `bash build/cover.sh tests/Basalt.Domain.Tests` y la puerta de cobertura de Aplicación cumplen
      los umbrales de `docs/specs/09-estrategia-de-pruebas.md`.
- [ ] La build iOS (`build-ios` en CI) compila el head `net10.0-ios` sin firma.
- [ ] `PROGRESS.md` refleja el estado real y no quedan tareas bloqueadas que afecten a la release.

## 2. Versión y etiqueta

- [ ] Actualiza la versión visible y la de build:
      - `src/Basalt.iOS/Basalt.iOS.csproj`: `ApplicationDisplayVersion` (por ejemplo `0.2`) y
        `ApplicationVersion` (entero incremental).
      - Versión del instalador/paquete Windows y de la app macOS.
- [ ] Revisa el changelog o las notas de la release.
- [ ] Crea el tag anotado y publícalo:

```bash
git tag -a v0.2.0 -m "Basalt 0.2.0"
git push origin v0.2.0
```

## 3. Empaquetado

### Windows

Ejecutar en Windows con el SDK y las herramientas de empaquetado instaladas.

```powershell
dotnet publish src/Basalt.Desktop/Basalt.Desktop.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishReadyToRun=true -o artifacts/win-x64
dotnet publish src/Basalt.Desktop/Basalt.Desktop.csproj -c Release -r win-arm64 `
  --self-contained true -p:PublishReadyToRun=true -o artifacts/win-arm64

Compress-Archive artifacts/win-x64/* artifacts/Basalt-<version>-win-x64.zip
Compress-Archive artifacts/win-arm64/* artifacts/Basalt-<version>-win-arm64.zip

# MSIX: empaquetar la salida con MakeAppx y firmar con el certificado de release.
# makeappx pack /d artifacts/win-x64 /p artifacts/Basalt-<version>-win-x64.msix
# signtool sign /fd SHA256 /f <certificado>.pfx /p <contraseña> artifacts/Basalt-<version>-win-x64.msix
```

- [ ] MSIX x64 y arm64 generados y firmados.
- [ ] ZIP portables x64 y arm64 generados.
- [ ] `signtool verify /pa` correcto en ambos MSIX.

### macOS

Ejecutar en macOS con Xcode y las herramientas de notarización.

```bash
dotnet publish src/Basalt.Desktop/Basalt.Desktop.csproj -c Release -r osx-arm64 --self-contained true -o artifacts/osx-arm64
dotnet publish src/Basalt.Desktop/Basalt.Desktop.csproj -c Release -r osx-x64 --self-contained true -o artifacts/osx-x64

# Construir Basalt.app universal (lipo del ejecutable y de las librerías nativas), copiar
# Info.plist, iconos y entitlements (com.apple.security.network.client).
codesign --force --deep --options runtime --sign "Developer ID Application: ..." Basalt.app

ditto -c -k --keepParent Basalt.app Basalt-<version>-macos-universal.zip
xcrun notarytool submit Basalt-<version>-macos-universal.zip --keychain-profile basalt --wait
xcrun stapler staple Basalt.app
spctl --assess --type execute --verbose Basalt.app

hdiutil create -volname Basalt -srcfolder Basalt.app -ov -format UDZO Basalt-<version>.dmg
```

- [ ] `.app` universal arm64 + x64 verificada con `lipo -info`.
- [ ] Firma con Developer ID y Hardened Runtime.
- [ ] Notarización aceptada y ticket grapado (`stapler validate`).
- [ ] `.dmg` generado y abierto al menos una vez en un Mac limpio.

### iOS

Ejecutar en macOS con Xcode y el perfil de aprovisionamiento.

```bash
dotnet workload restore
dotnet publish src/Basalt.iOS/Basalt.iOS.csproj -c Release -f net10.0-ios \
  -p:RuntimeIdentifier=ios-arm64 \
  -p:CodesignKey="Apple Distribution: ..." \
  -p:CodesignProvision="Basalt App Store" \
  -p:ArchiveOnBuild=true
```

- [ ] `Info.plist` conserva `UIFileSharingEnabled`, `LSSupportsOpeningDocumentsInPlace`,
      `BGTaskSchedulerPermittedIdentifiers` (`net.basalt.sync.refresh`), los `UIBackgroundModes` y
      `ITSAppUsesNonExemptEncryption=false`.
- [ ] `.ipa` firmado con el perfil de distribución y subido a TestFlight.
- [ ] La build de TestFlight arranca y muestra el selector de vaults.

## 4. Pruebas de humo antes de publicar

Repetir en cada plataforma empaquetada:

- [ ] Crear un vault y abrir una carpeta existente como vault.
- [ ] Crear una nota, escribir, comprobar el autoguardado y reabrir la app.
- [ ] Crear un enlace `[[...]]`, renombrar la nota destino y comprobar que el enlace se actualiza.
- [ ] Buscar en el vault y abrir el resultado.
- [ ] Conectar una cuenta de GitHub y configurar un repositorio de prueba.
- [ ] Primer ciclo (subida), segundo ciclo (sin cambios) y un cambio desde otro dispositivo.
- [ ] Provocar un conflicto de texto y resolverlo desde el Centro de conflictos.
- [ ] Abrir el historial de una nota y restaurar una versión.
- [ ] Pausar, reanudar y deshabilitar la sincronización sin perder ficheros.
- [ ] Revisar que no hay texto sin traducir en `en` y `es`.
- [ ] Confirmar que el token de prueba no aparece en `{AppData}`, logs ni vault.

## 5. Publicación

- [ ] Adjunta a la release de GitHub: MSIX x64/arm64, ZIP portables, `.dmg` y notas de la versión.
- [ ] Genera y publica los hashes SHA-256 de todos los artefactos.
- [ ] Publica la ficha de la App Store con las notas de la versión.
- [ ] Envía la build a TestFlight y espera la revisión de Apple si aplica.
- [ ] Anuncia la versión con enlaces de descarga.

## 6. Verificación posterior

- [ ] Descarga los artefactos publicados y comprueba los hashes.
- [ ] Instala desde cada artefacto en una máquina/instalación limpia.
- [ ] Comprueba que la app arranca, crea un vault y sincroniza.
- [ ] Revisa los logs de la primera ejecución: sin errores ni tokens.
- [ ] Vigila los issues durante 48 h.

## 7. Rollback

Si un artefacto está corrupto o introduce pérdida de datos:

- [ ] Retira la release o marca los artefactos como retirados.
- [ ] No reescribas el tag ya publicado; crea una versión de parche.
- [ ] Si el problema afecta a la sincronización, deshabilita primero el sync del vault afectado;
      los ficheros locales y el repositorio remoto se conservan.
- [ ] Documenta la causa y el seguimiento en `PROGRESS.md` y, si cambia el comportamiento, en la
      especificación.
