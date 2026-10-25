# 07 — Interfaz de usuario (Avalonia)

## 1. Principios

- **MVVM estricto**: `Basalt.Presentation` (ViewModels, sin Avalonia) + `Basalt.UI` (Views XAML compiladas, `x:CompileBindings="True"` y `x:DataType` obligatorio en todas las vistas).
- Las vistas no contienen lógica de negocio; el code-behind solo se permite para: enfoque, integración con controles de terceros (AvaloniaEdit), gestos y animaciones.
- Localización: todos los textos mediante `ILocalizer` / extensión de marcado `{l:Loc Key}`; recursos `Strings.resx` (en) y `Strings.es.resx`.
- Tema: `FluentTheme` con paleta propia (claro/oscuro), tokens de diseño en `Basalt.UI/Themes/Tokens.axaml` (colores, espaciados, radios, tipografía). Ningún color literal fuera de los tokens. El sistema visual de referencia está en `docs/design/` (maqueta y `basalt.tokens.json`, que refleja `Tokens.axaml` 1:1): acento violeta, grises neutros, superficies `Chrome` (ribbon, pestañas, barra de estado) y `Sidebar` en oscuro.
- Tipografía: Inter para la interfaz, **Literata** para el modo lectura (cuerpo 17 con interlineado 1,7; encabezados desde 24) y **JetBrains Mono** para código; Literata y JetBrains Mono van incluidas en `Basalt.UI/Assets/Fonts` (OFL) con las fuentes del sistema como respaldo.
- Componentes del sistema (`Themes/Styles.axaml`): botones primario (`accent`), secundario (por defecto), discreto (`quiet`) y destructivo (`danger`); chips (`chip`, `chip tag`), insignias (`badge`), tarjetas (`card`), interruptor segmentado (`segmented`), pestañas subrayadas (`tab`) y la píldora de sincronización (`sync-pill` con tono `success`/`accent`/`warning`/`danger` según `SyncStatusViewModel.Tone`).
- Accesibilidad: `AutomationProperties.Name` en todos los controles interactivos sin texto, orden de tabulación lógico, navegación completa con teclado en escritorio, contraste AA.

## 2. ViewModels (Presentation)

Basados en `CommunityToolkit.Mvvm` (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]` con `AsyncRelayCommand` cancelables).

```
ShellViewModel                          (ventana / raíz iOS)
├─ VaultPickerViewModel                 (lista de vaults, crear, abrir, clonar)
│   └─ CloneFromGitHubViewModel
├─ WorkspaceViewModel                   (un vault abierto)
│   ├─ LeftSidebarViewModel
│   │   ├─ FileExplorerViewModel  ─ FileTreeNodeViewModel (virtualizado, carga perezosa)
│   │   ├─ SearchPanelViewModel   ─ SearchResultGroupViewModel
│   │   ├─ BookmarksViewModel
│   │   └─ TagsPanelViewModel
│   ├─ RightSidebarViewModel
│   │   ├─ BacklinksViewModel
│   │   ├─ OutgoingLinksViewModel
│   │   ├─ OutlineViewModel
│   │   ├─ LocalGraphViewModel
│   │   └─ PropertiesViewModel
│   ├─ PaneLayoutViewModel              (árbol de splits)
│   │   └─ TabGroupViewModel
│   │       └─ TabViewModel (abstracto) ← NoteTabViewModel, GraphTabViewModel, ImageTabViewModel,
│   │                                        ConflictResolutionTabViewModel, FileHistoryTabViewModel,
│   │                                        SettingsTabViewModel (iOS)
│   ├─ StatusBarViewModel               (palabras, sync, modo editor)
│   └─ SyncStatusViewModel
├─ QuickSwitcherViewModel  (overlay)
├─ CommandPaletteViewModel (overlay)
└─ Settings: SettingsViewModel → secciones (GeneralSettingsVM, EditorSettingsVM, FilesLinksSettingsVM,
             AppearanceSettingsVM, HotkeysSettingsVM, DailyNotesSettingsVM, TemplatesSettingsVM,
             AccountsSettingsVM, VaultSyncSettingsVM (uno por vault), AboutVM)
   Diálogos: GitHubSignInViewModel (device flow), ConfigureSyncWizardViewModel (pasos: cuenta → repo → opciones → dry-run → confirmar)
```

Reglas:
- Un ViewModel no conoce otros ViewModels hermanos: se comunican mediante `IEventBus` de aplicación o un `IWorkspaceMessenger` (mensajería de UI, `WeakReferenceMessenger` de CommunityToolkit envuelto en interfaz).
- La navegación (abrir nota en pestaña, enfocar panel) se hace con `IWorkspaceNavigator` (Presentation), implementado por `WorkspaceViewModel`.
- Factorías de ViewModels por interfaz (`INoteTabFactory`), para Open/Closed con nuevos tipos de pestaña (`IPaneFactory` registrados en DI).
- Toda operación > 100 ms muestra estado ocupado; > 2 s ofrece cancelación.
- Los ViewModels exponen `IReadOnlyList`/`ObservableCollection` actualizadas solo en el hilo de UI (vía `IUiDispatcher`).
- Los ViewModels son **100 % testeables sin Avalonia** (ver [09](09-estrategia-de-pruebas.md) §5).

## 3. Diseño de pantalla (escritorio)

```
┌──────────────────────────────────────────────────────────────────────────────────────────┐
│ [≡] [←][→]  Pestaña 1 × │ Pestaña 2 × │ +                              [⟳ sync] [⚙]       │
├──┬──────────────────┬──────────────────────────────────────────────┬───────────────────────┤
│📁│ Explorador       │  Título de la nota                            │ Backlinks │ Esquema   │
│🔍│  ▸ Carpeta       │  ┌ Propiedades (plegable) ────────────────┐   │ ───────────────────── │
│🔖│  ▾ Proyectos     │  │ status: draft   tags: [a, b]           │   │ Menciones enlazadas   │
│#️⃣│    · Nota A      │  └────────────────────────────────────────┘   │  ▸ Nota X (2)         │
│📅│    · Nota B      │  # Encabezado                                  │ Menciones no enlazadas│
│🕸│                  │  Texto con [[enlace]] y #etiqueta              │  ▸ Nota Y  [Enlazar]  │
│  │                  │                                                │ Grafo local           │
├──┴──────────────────┴──────────────────────────────────────────────┴───────────────────────┤
│ Vault: Personal · ✓ Sincronizado hace 2 min · 3 conflictos           123 palabras · Fuente │
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

- Ribbon vertical izquierdo con accesos (explorador, búsqueda, marcadores, etiquetas, nota diaria, grafo; abajo conflictos con contador, historial, paleta, ajustes y la barra derecha). Los botones de paneles marcan el panel activo.
- Barra izquierda: cabecera con la inicial y el nombre del vault y un acceso al selector rápido; debajo, el panel activo. Barra derecha: pestañas subrayadas (enlaces entrantes, salientes, esquema, grafo local, propiedades).
- Cabecera de la nota: ruta de carpetas (`TabViewModel.Folders`) y el título, con el interruptor **Fuente / Lectura** (`ShowSourceCommand` / `ShowReadingCommand`).
- Barra de estado: píldora de sincronización siempre visible (estado, "N cambios pendientes", conflictos) con sincronizar ahora y pausar/reanudar; a la derecha, el fichero activo.
- Sidebars redimensionables con `GridSplitter`, plegables, recuerdan ancho.
- macOS: menú de aplicación nativo (`NativeMenu`) con Archivo/Editar/Ver/Ventana/Ayuda; barra de título integrada (`ExtendClientAreaToDecorationsHint`) con semáforo a la izquierda.
- Windows: barra de título personalizada con botones del sistema; menú en el botón ≡.
- Multi-ventana: una `MainWindow` por vault abierto (escritorio).

## 4. iOS (iPhone / iPad)

- `ISingleViewApplicationLifetime`; `MainView` raíz.
- **iPhone**: navegación en pila: Lista de vaults → Explorador → Nota. Barra inferior: explorador, búsqueda, nota diaria, nueva nota, más (paleta, marcadores, etiquetas, grafo, ajustes). Paneles derechos (backlinks, esquema, propiedades) como hoja inferior (`BottomSheet` propio) desde un botón en la barra de la nota.
- **iPad**: diseño de dos/tres columnas similar a escritorio en horizontal; una columna + hoja en vertical estrecho. Soporte de teclado físico con atajos.
- **Barra de accesorios del teclado** en el editor: `[[`, `#`, `- [ ]`, deshacer, rehacer, sangrar, desangrar, ocultar teclado.
- Respetar safe areas (`TopLevel.InsetsManager`), modo oscuro del sistema, Dynamic Type (escala de fuente base según `UIContentSizeCategory` vía adaptador de plataforma).
- Ciclo de vida: al pasar a segundo plano → flush de ediciones + disparador `AppBackgrounding` con `beginBackgroundTask` para obtener tiempo extra.

## 5. Editor

### 5.1 Control

- **Escritorio**: `AvaloniaEdit.TextEditor` envuelto en `MarkdownEditorControl` (Basalt.UI):
  - Resaltado: `AvaloniaEdit.TextMate` con gramática Markdown + inyecciones para wikilinks, etiquetas, highlights, comentarios y callouts (gramática propia `basalt-markdown.tmLanguage.json`). Colores desde tokens del tema.
  - `IVisualLineTransformer` para atenuar la sintaxis (`**`, `[[`, `]]`) y aumentar tamaño de encabezados.
  - Plegado por encabezados y bloques de código (`FoldingManager` + estrategia propia).
  - Ventana de autocompletado (`CompletionWindow`) alimentada por `SuggestLinkTargets` / `SuggestTags`.
  - Enlace bidireccional del texto con el ViewModel mediante un `IEditorBridge` (el ViewModel no tiene el texto completo como propiedad enlazada para evitar copias en cada pulsación; recibe `TextChanged` con debounce y pide el texto cuando lo necesita).
- **iOS**: riesgo técnico identificado — el soporte de IME/teclado táctil, selección y lupa de AvaloniaEdit en iOS debe validarse en la **tarea de spike 0.4** del plan. Si no es satisfactorio, se usa un `MarkdownTextBoxEditor` basado en `TextBox` multilinea de Avalonia con resaltado simplificado (sin plegado). Ambos implementan la misma interfaz `IMarkdownEditorView` para que `NoteTabViewModel` sea idéntico.

### 5.2 Comportamientos del editor (lógica en Presentation/Domain, no en la vista)

`EditorCommandsService` (Presentation, puro y testeable sobre un modelo `TextBufferState { Text, SelectionStart, SelectionLength }`):
- Continuar listas/tareas/citas con Enter; terminar lista con Enter en elemento vacío.
- Tab / Shift+Tab: sangrar/desangrar elementos de lista.
- Alternar negrita/cursiva/código/tachado/resaltado sobre la selección (o insertar marcadores vacíos con el cursor dentro).
- Alternar tarea en la línea actual.
- Insertar enlace (`Ctrl+K`): si hay selección que parece URL → `[](url)`; si no → `[[selección]]`.
- Autocierre de pares.
Cada comportamiento es una función pura `TextBufferState → TextEdit[]` con tests unitarios tabulares.

## 6. Modo lectura (renderizado)

- `IMarkdownRenderer` produce un **modelo de render propio** (`RenderDocument` con bloques: `HeadingBlock`, `ParagraphBlock`, `ListBlock`, `TaskItemBlock`, `QuoteBlock`, `CalloutBlock`, `CodeBlock`, `TableBlock`, `ImageBlock`, `EmbedBlock`, `ThematicBreak`, `FootnotesBlock`; inlines: `Text`, `Emphasis`, `Strong`, `Code`, `Link(Internal|External)`, `Tag`, `Highlight`, `Strikethrough`, `LineBreak`).
- `Basalt.UI` contiene `RenderDocumentView`, que convierte el modelo a controles Avalonia (`SelectableTextBlock` con `Inlines`, `Border`, `Grid` para tablas, `Image`), virtualizado por bloques (`ItemsRepeater`) para notas largas.
- Embeds de notas: renderizado recursivo con límite de profundidad 3 y detección de ciclos; embeds de encabezado/bloque muestran solo esa sección.
- Imágenes: carga asíncrona con caché LRU de bitmaps decodificados (máx. 100 MB); tamaño `|300` o `|300x200` respetado.
- Casillas de tareas clicables → `ToggleTask`.
- Bloques de código con resaltado (TextMate) y botón copiar.

## 7. Vista de grafo

- Control `GraphCanvas : Control` que dibuja con `DrawingContext` en `Render` (sin un control por nodo).
- La simulación (`ForceLayout` del dominio) se ejecuta en un hilo de fondo a 60 Hz mientras no sea estable; la vista lee instantáneas de posiciones (doble búfer) y se invalida con `DispatcherTimer`/`TopLevel.RequestAnimationFrame`.
- Interacción: rueda/pinch = zoom, arrastrar fondo = pan, arrastrar nodo = fijar mientras se arrastra, hover resalta vecinos y atenúa el resto, clic abre nota (Ctrl/Cmd+clic en nueva pestaña). Etiquetas de nodos visibles a partir de cierto zoom.
- Panel de ajustes del grafo (filtros, grupos de color, fuerzas) enlazado a `GraphTabViewModel`.

## 8. Sincronización en la UI

- `SyncStatusViewModel` por vault; icono en barra de estado (escritorio) o en cabecera del explorador (iOS) con menú: "Sincronizar ahora", "Pausar/Reanudar", "Ver registro", "Conflictos (n)", "Ajustes de sincronización".
- **Asistente `ConfigureSyncWizard`**: (1) cuenta (o añadir) → (2) repositorio: crear nuevo (nombre sugerido = nombre del vault en kebab-case, privado) o elegir existente (lista con búsqueda) → (3) rama y opciones (disparadores, políticas, alcance) → (4) dry-run con resumen → (5) confirmar y primera sincronización con progreso.
- **Diálogo de inicio de sesión** (device flow): código grande y copiable, botón "Abrir GitHub", indicador de espera, cuenta atrás de caducidad, cancelar.
- Notificaciones (toasts) para: conflictos nuevos, errores persistentes, re-autenticación necesaria. Nunca para sincronizaciones exitosas rutinarias.
- **Centro de conflictos** (`ConflictResolutionTabViewModel`) y **Historial** (`FileHistoryTabViewModel`) con visor de diff (`DiffView` control: dos/tres columnas, alineado por hunks del `LineDiff` del dominio).

## 9. Comandos y atajos

- `ICommandRegistry` (Application) con `CommandDescriptor { Id, TitleKey, Category, DefaultGesture(Windows/macOS), CanExecute(context), ExecuteAsync(context) }`. Los módulos aportan comandos mediante `ICommandContribution` (Open/Closed).
  - `DefaultGesture` es una cadena con notación `Mod` (`Mod+Shift+T`): `Mod` = Ctrl en Windows y Cmd en macOS, de modo que un único valor sirve para ambas plataformas; `Gesture.Parse(texto, plataforma)` la resuelve. Ids duplicados → error de programación al construir el registro.
  - `CommandContext(Vault?, ActiveFile?, Host)`: `ICommandHost` es lo que un comando puede pedir a la UI (abrir una nota); lo implementa Presentation. Los comandos de Aplicación (`CoreCommands`: nueva nota, nota diaria de hoy, marcar fichero activo) ejecutan casos de uso; los que actúan sobre vistas los aporta Presentation.
- `KeyBindingService` (Presentation) resuelve gestos → comandos con preferencia de las asignaciones del usuario (`IAppSettingsStore`), detecta conflictos.
- La vista traduce `KeyEventArgs` → `KeyGesture` neutral (`Basalt.Presentation.Input.Gesture`) para mantener Presentation sin Avalonia.

## 10. Rendimiento de UI

- Explorador y resultados de búsqueda virtualizados (`TreeDataGrid` o `ItemsRepeater` con aplanado del árbol).
- Ninguna operación de E/S ni análisis en el hilo de UI (verificado en tests de ViewModel con `IUiDispatcher` que falla si se invoca trabajo pesado — y con un analizador que prohíbe `.Result`/`.Wait()`).
- Apertura de una nota de 1 MB: < 200 ms hasta texto visible.
