# 0007 — Framework de tests de UI headless

- Estado: Aceptado
- Fecha: 2026-10-19

## Contexto
`Avalonia.Headless.XUnit` 12.1.4 está compilado contra xUnit v3 3.x; con xUnit v3 4.0.2 (el resto de la solución, requerido por `FsCheck.Xunit.v3` 3.5) falla en el descubrimiento con `MissingMethodException`.

## Decisión
`Basalt.UI.Tests` usa xUnit v3 4.x con **`Avalonia.Headless`** (independiente del framework) y un helper propio `Headless.RunAsync(...)` que despacha el cuerpo del test en una `HeadlessUnitTestSession` compartida. Los tests son `[Fact]` normales que devuelven `Task`.

## Consecuencias
- ✅ Una sola versión de xUnit en toda la solución; los fakes de `Basalt.Testing` se reutilizan en tests de UI.
- ⚠️ Hay que envolver cada test de UI en `Headless.RunAsync`.

## Alternativas consideradas
- Bajar toda la solución a xUnit v3 3.x: incompatible con FsCheck.Xunit.v3 3.5.
- `Avalonia.Headless.NUnit` en un proyecto aparte: dos frameworks de test sin necesidad.
