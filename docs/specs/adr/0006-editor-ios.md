# 0006 — Editor en iOS

- Estado: Propuesto (validación en simulador pendiente: requiere macOS)
- Fecha: 2026-10-19

## Contexto
T0.4 pide validar en el simulador de iOS el teclado, IME (acentos, emoji), selección y desplazamiento de `AvaloniaEdit.TextEditor` (Avalonia 12.1 / AvaloniaEdit 12.0). El desarrollo actual se hace en Windows (ADR-0008) y no hay un Mac disponible, por lo que el spike en simulador no puede ejecutarse todavía.

## Decisión (provisional y conservadora)
- `NoteTabViewModel` depende solo de `IMarkdownEditorView` (07 §5.1), de modo que el editor es intercambiable sin tocar Presentation.
- Escritorio: `MarkdownEditorControl` sobre AvaloniaEdit.
- iOS: hasta completar la validación se usará `MarkdownTextBoxEditor` (TextBox multilínea con resaltado simplificado), que usa el soporte de IME nativo de Avalonia; es la opción que no arriesga pérdida de pulsaciones.
- Se verificó en Windows (headless) que AvaloniaEdit 12.0 se instancia y edita con Avalonia 12.1.4.

## Consecuencias
- Si la validación en simulador es satisfactoria, iOS pasará a AvaloniaEdit (este ADR pasará a Aceptado con el resultado).
- T0.4 queda parcialmente bloqueado en `PROGRESS.md` hasta disponer de macOS.
