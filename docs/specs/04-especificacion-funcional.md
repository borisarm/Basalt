# 04 — Especificación funcional

Cada funcionalidad tiene un identificador `F-XXX`. **Cada escenario Gherkin DEBE tener al menos un test automatizado** cuyo nombre o atributo lo referencie: `[Trait("Spec", "F-030.2")]`. El test de trazabilidad (ver [09](09-estrategia-de-pruebas.md) §9) falla si algún escenario de este documento no está cubierto.

Formato de referencia de escenarios: `F-XXX.n` (n = orden del escenario dentro de la funcionalidad).

---

## F-001 Gestión de vaults

**Descripción.** Pantalla de inicio (vault picker) con la lista de vaults registrados en el dispositivo, su ubicación y su estado de sincronización. Acciones: crear vault, abrir carpeta como vault, clonar desde GitHub, quitar de la lista, revelar en el gestor de ficheros (escritorio).

```gherkin
Escenario: F-001.1 Crear un vault nuevo
  Dado que no existe la carpeta "Notas" en la ubicación elegida
  Cuando creo un vault llamado "Notas"
  Entonces se crea la carpeta "Notas" con ".basalt/vault.json" que contiene un VaultId nuevo
  Y el vault aparece en la lista y se abre

Escenario: F-001.2 Nombre de vault inválido
  Cuando intento crear un vault llamado "a/b"
  Entonces veo el error "vault.invalid_name" y no se crea nada

Escenario: F-001.3 Abrir carpeta existente sin configuración
  Dada una carpeta con notas Markdown y sin ".basalt"
  Cuando la abro como vault
  Entonces se crea ".basalt/vault.json" y se indexan todas las notas

Escenario: F-001.4 Abrir carpeta que ya es un vault de otro dispositivo
  Dada una carpeta con ".basalt/vault.json" con VaultId X
  Cuando la abro como vault
  Entonces se registra con VaultId X (no se genera uno nuevo)

Escenario: F-001.5 Quitar de la lista no borra ficheros
  Cuando quito un vault de la lista
  Entonces desaparece de la lista, su perfil de sync y su índice local se eliminan
  Y la carpeta y sus ficheros permanecen intactos

Escenario: F-001.6 Vault no encontrado
  Dado un vault registrado cuya carpeta ya no existe
  Cuando lo intento abrir
  Entonces veo "vault.location_missing" con opciones "Localizar…" y "Quitar de la lista"

Escenario: F-001.7 Varios vaults abiertos (escritorio)
  Cuando abro un segundo vault
  Entonces se abre en una ventana nueva y ambos operan y sincronizan de forma independiente
```

En iOS solo hay un vault abierto a la vez; cambiar de vault cierra el anterior (su sincronización programada sigue activa).

## F-002 Explorador de ficheros

Árbol de carpetas y ficheros del vault. Oculta `.basalt/`, `.trash/`, ficheros ocultos (`.xxx`) y temporales. Muestra notas sin extensión `.md`; los adjuntos con extensión. Orden configurable. Soporta multiselección (Ctrl/Cmd, Shift), arrastrar y soltar para mover (también soltar ficheros externos = importar), menú contextual: nueva nota, nueva carpeta, renombrar (F2), eliminar (Supr), duplicar, copiar ruta, revelar en gestor de ficheros, abrir en nueva pestaña, marcar como favorito.

```gherkin
Escenario: F-002.1 Crear nota en carpeta
  Dado que tengo seleccionada la carpeta "Proyectos"
  Cuando ejecuto "Nueva nota"
  Entonces se crea "Proyectos/Untitled.md", se abre en el editor y el título queda en modo edición

Escenario: F-002.2 Nombre único automático
  Dado que existen "Untitled.md" y "Untitled 1.md"
  Cuando creo una nota nueva en la misma carpeta
  Entonces se llama "Untitled 2.md"

Escenario: F-002.3 Renombrar a nombre existente
  Cuando renombro "A.md" a "B" y "B.md" ya existe
  Entonces veo "path.already_exists" y no cambia nada

Escenario: F-002.4 Renombrar solo mayúsculas
  Cuando renombro "nota.md" a "Nota"
  Entonces el fichero pasa a llamarse "Nota.md" también en sistemas insensibles a mayúsculas

Escenario: F-002.5 Mover por arrastrar
  Cuando arrastro "A.md" a la carpeta "Archivo"
  Entonces "A.md" pasa a "Archivo/A.md" y los enlaces que apuntaban a ella se actualizan si es necesario

Escenario: F-002.6 Eliminar a la papelera del vault
  Cuando elimino "A.md" con la opción por defecto
  Entonces se mueve a ".trash/A.md" (con sufijo si existe) tras confirmar
  Y deja de aparecer en el explorador, búsqueda y grafo

Escenario: F-002.7 Cambios externos
  Cuando otra aplicación crea "Externa.md" en el vault
  Entonces aparece en el explorador en menos de 1 segundo y se indexa
```

## F-003 Editor

- Modo **fuente**: AvaloniaEdit con resaltado Markdown (encabezados, negrita/cursiva, código, enlaces, etiquetas, frontmatter, callouts), números de línea opcionales, ajuste de línea, plegado por encabezados, indentación con Tab/Shift+Tab en listas, continuación automática de listas y tareas al pulsar Enter, cierre automático de `[[`, `**`, `` ` ``, `(`.
- Modo **lectura**: documento renderizado (ver [07](07-interfaz-usuario.md) §6). Alternar con `Ctrl/Cmd+E`.
- **Autoguardado**: 2 s después de la última pulsación y al perder foco/cambiar de nota/cerrar. Nunca se pierde una edición al cerrar la app (flush síncrono acotado en el evento de cierre).
- Ctrl/Cmd+clic sobre enlace abre destino; clic en enlace no resuelto crea la nota.
- Pegar imagen → `ImportAttachment` + inserción de `![[Pasted image 20261019154233.png]]`.
- Búsqueda/reemplazo en la nota (`Ctrl/Cmd+F`, `Ctrl/Cmd+H`).
- Contador de palabras y caracteres en barra de estado.

```gherkin
Escenario: F-003.1 Autoguardado
  Dado que edito una nota
  Cuando pasan 2 segundos sin escribir
  Entonces el contenido se escribe en disco de forma atómica y el indicador de "sin guardar" desaparece

Escenario: F-003.2 Modificación externa sin cambios locales
  Dada una nota abierta sin cambios pendientes
  Cuando el fichero cambia en disco (otra app o sincronización)
  Entonces el editor recarga el contenido conservando la posición del cursor lo más cerca posible

Escenario: F-003.3 Modificación externa con cambios locales pendientes
  Dada una nota abierta con cambios sin guardar
  Cuando el fichero cambia en disco
  Entonces se realiza una fusión de tres vías (base = último contenido guardado/cargado)
  Y si es limpia se aplica en el editor; si no, se muestra un aviso con "Ver diferencias", "Mantener el mío", "Usar el de disco"

Escenario: F-003.4 Continuación de lista
  Dado que el cursor está al final de "- [ ] tarea"
  Cuando pulso Enter
  Entonces se inserta una nueva línea "- [ ] "
  Y si pulso Enter de nuevo sobre el elemento vacío, se elimina el marcador

Escenario: F-003.5 Pegar imagen
  Cuando pego una imagen PNG desde el portapapeles
  Entonces se guarda en la carpeta de adjuntos configurada y se inserta un embed en el cursor
```

## F-004 Enlaces, autocompletado y renombrado

```gherkin
Escenario: F-004.1 Autocompletado de notas
  Cuando escribo "[[pro"
  Entonces veo sugerencias difusas de notas y alias que contienen "pro", ordenadas por puntuación
  Y al aceptar se inserta "[[Proyecto X]]" con la ruta mínima no ambigua

Escenario: F-004.2 Autocompletado de encabezados
  Cuando escribo "[[Proyecto X#"
  Entonces veo los encabezados de "Proyecto X"

Escenario: F-004.3 Autocompletado de bloques
  Cuando escribo "[[Proyecto X#^"
  Entonces veo párrafos de "Proyecto X"; al aceptar uno sin id, se le añade "^<id aleatorio de 6 caracteres>" en el fichero destino

Escenario: F-004.4 Actualización de enlaces al renombrar
  Dado que "A.md" contiene "[[B]]", "[[B#Sección|alias]]" y "[texto](B.md)"
  Cuando renombro "B.md" a "C.md"
  Entonces "A.md" contiene "[[C]]", "[[C#Sección|alias]]" y "[texto](C.md)"
  Y el resto del contenido de "A.md" es idéntico byte a byte

Escenario: F-004.5 Renombrar carpeta
  Cuando renombro la carpeta "X" a "Y"
  Entonces todos los enlaces con ruta explícita "X/..." pasan a "Y/..."

Escenario: F-004.6 Enlaces en bloques de código no se tocan
  Dado que "A.md" tiene "[[B]]" dentro de un bloque de código
  Cuando renombro "B.md"
  Entonces el bloque de código no cambia

Escenario: F-004.7 Ambigüedad tras mover
  Dado que existen "x/Nota.md" y "y/Nota.md" y "A.md" enlaza "[[x/Nota]]"
  Cuando muevo "y/Nota.md" a "z/Nota.md"
  Entonces "A.md" no cambia
```

## F-005 Backlinks y enlaces salientes

Panel lateral con dos secciones: **Menciones enlazadas** (agrupadas por nota origen, con contexto de la línea/párrafo y resaltado) y **Menciones no enlazadas** (apariciones del título o alias como palabra completa, insensible a mayúsculas, fuera de enlaces y código) con botón "Enlazar". Panel de **Enlaces salientes** con resueltos y no resueltos.

```gherkin
Escenario: F-005.1 Backlinks
  Dado que "A.md" y "B.md" enlazan a "C"
  Cuando abro "C.md"
  Entonces el panel muestra 2 notas con su contexto

Escenario: F-005.2 Mención no enlazada
  Dado que "A.md" contiene "hablé de proyecto x ayer"
  Cuando abro "Proyecto X.md" y pulso "Enlazar" en esa mención
  Entonces "A.md" contiene "hablé de [[Proyecto X|proyecto x]] ayer"

Escenario: F-005.3 Actualización en vivo
  Cuando añado "[[C]]" en "D.md" y se guarda
  Entonces el panel de backlinks de "C.md" se actualiza en menos de 500 ms
```

## F-006 Etiquetas

Panel con árbol de etiquetas y contadores; clic → búsqueda `tag:#x`. Etiquetas desde texto (`#x`) y frontmatter (`tags: [x, y]` o lista YAML). Autocompletado al escribir `#`.

```gherkin
Escenario: F-006.1 Etiquetas anidadas
  Dado "#trabajo/cliente-a" en una nota y "#trabajo" en otra
  Entonces el panel muestra "trabajo (2)" con hijo "cliente-a (1)"

Escenario: F-006.2 No son etiquetas
  Entonces "#123", "#" solo, "C#" dentro de una palabra, "#x" en código y los encabezados "# Título" no cuentan como etiquetas
```

## F-007 Búsqueda

Panel de búsqueda con caja de consulta, opciones (sensible a mayúsculas, orden, explicación de consulta), resultados agrupados por nota con coincidencias y contexto. Sintaxis en [08](08-persistencia-e-indexacion.md) §4.

```gherkin
Escenario: F-007.1 Términos múltiples (AND)
  Cuando busco "reunión cliente"
  Entonces solo aparecen notas que contienen ambos términos

Escenario: F-007.2 Operadores
  Cuando busco 'tag:#trabajo -path:Archivo "fecha límite" OR urgente'
  Entonces los resultados respetan la precedencia definida (OR liga más fuerte que AND implícito, como en Obsidian)

Escenario: F-007.3 Regex
  Cuando busco "/\d{4}-\d{2}-\d{2}/"
  Entonces aparecen notas con fechas ISO y las coincidencias resaltadas

Escenario: F-007.4 Consulta inválida
  Cuando busco "/[a-/"
  Entonces veo un error de sintaxis inline sin bloquear la UI

Escenario: F-007.5 Rendimiento
  Dado un vault de 10 000 notas
  Cuando busco un término
  Entonces los primeros resultados aparecen en menos de 300 ms
```

## F-008 Quick switcher y paleta de comandos

- **Quick switcher** (`Ctrl/Cmd+O`): búsqueda difusa por título, alias y ruta; muestra recientes si vacío; `Enter` abre, `Ctrl/Cmd+Enter` abre en nueva pestaña, `Shift+Enter` crea nota con el texto tecleado.
- **Paleta de comandos** (`Ctrl/Cmd+P`): todos los comandos registrados (`ICommandRegistry`) con su atajo; búsqueda difusa; recientes primero.

```gherkin
Escenario: F-008.1 Crear desde quick switcher
  Cuando escribo "Idea nueva" y no existe ninguna nota con ese nombre
  Entonces la primera opción es "Crear Idea nueva" y al pulsar Enter se crea y abre

Escenario: F-008.2 Comando ejecutado
  Cuando ejecuto "Alternar modo lectura" desde la paleta
  Entonces el efecto es idéntico al atajo de teclado
```

## F-009 Notas diarias y plantillas

```gherkin
Escenario: F-009.1 Abrir nota de hoy
  Dado formato "YYYY-MM-DD", carpeta "Diario" y plantilla "Plantillas/Diario.md" con "# {{date:dddd D MMMM YYYY}}"
  Y la fecha local es 2026-10-19
  Cuando ejecuto "Abrir nota diaria de hoy"
  Entonces se crea "Diario/2026-10-19.md" con "# lunes 19 octubre 2026" (cultura es-ES) y se abre

Escenario: F-009.2 Nota diaria existente
  Cuando la nota de hoy ya existe
  Entonces se abre sin modificarla

Escenario: F-009.3 Insertar plantilla
  Cuando inserto una plantilla en la nota "Reunión"
  Entonces "{{title}}" se sustituye por "Reunión" y el texto se inserta en el cursor
  Y si la plantilla tiene frontmatter y la nota también, las propiedades se fusionan (las de la nota prevalecen)
```

## F-010 Propiedades (frontmatter)

Panel de propiedades encima del editor (plegable) con edición tipada: texto, lista, número, checkbox, fecha, fecha-hora. Los tipos se infieren por valor y se pueden fijar por clave a nivel de vault (`.basalt/types.json`, sincronizado).

```gherkin
Escenario: F-010.1 Editar propiedad preserva el documento
  Cuando cambio "status: draft" a "status: done" en el panel
  Entonces solo cambia esa línea del fichero

Escenario: F-010.2 YAML inválido
  Dado un frontmatter con YAML inválido
  Entonces el panel muestra "Propiedades no válidas" con el error y el editor fuente sigue funcionando
```

## F-011 Esquema, marcadores y recientes

- Esquema: árbol de encabezados de la nota activa; clic desplaza el editor; resalta la sección actual.
- Marcadores: notas, carpetas, búsquedas y encabezados; grupos; persistidos en `.basalt/bookmarks.json` (sincronizado).
- Ficheros recientes: últimos 50 por vault, por dispositivo.

```gherkin
Escenario: F-011.1 Marcador sobrevive a renombrado
  Dado un marcador a "A.md"
  Cuando renombro "A.md" a "B.md"
  Entonces el marcador apunta a "B.md"
```

## F-012 Vista de grafo

- Grafo global (pestaña) y grafo local (panel lateral, profundidad 1–5).
- Filtros: búsqueda, etiquetas, adjuntos, existentes solamente, huérfanos. Grupos de color por consulta.
- Fuerzas configurables. Zoom, desplazamiento, arrastrar nodos, hover resalta vecinos, clic abre nota.
- Rendimiento: 5 000 nodos / 20 000 aristas a ≥ 30 fps en escritorio de referencia.

```gherkin
Escenario: F-012.1 Grafo local
  Dado que "A" enlaza "B" y "B" enlaza "C"
  Cuando abro el grafo local de "A" con profundidad 1
  Entonces veo los nodos "A" y "B" solamente

Escenario: F-012.2 Nodos no resueltos
  Dado que "A" enlaza "[[Inexistente]]"
  Cuando el filtro "Solo existentes" está desactivado
  Entonces aparece el nodo "Inexistente" con estilo de no resuelto
```

## F-013 Pestañas, paneles y espacio de trabajo (escritorio + iPad)

- Pestañas con historial atrás/adelante por pestaña (`Alt+←/→` / `Cmd+[ ]`), fijar pestaña, cerrar (`Ctrl/Cmd+W`), reabrir cerrada (`Ctrl/Cmd+Shift+T`).
- División vertical/horizontal del área central; barras laterales izquierda (explorador, búsqueda, marcadores, etiquetas) y derecha (backlinks, salientes, esquema, grafo local, propiedades) plegables y redimensionables.
- El diseño se guarda por vault y por dispositivo en `.basalt/workspace.{deviceId}.json` (**excluido de la sincronización** por defecto) y se restaura al abrir.
- iPhone: navegación tipo pila con una nota a la vez; paneles laterales como hojas deslizables.

## F-014 Ajustes

Ventana/pantalla de ajustes con secciones: General (idioma, abrir último vault), Editor (fuente, tamaño, ancho legible, números de línea, ajuste de línea, corrector ortográfico — escritorio, autocierre), Ficheros y enlaces (todas las `VaultSettings`), Apariencia (tema claro/oscuro/sistema, color de acento, tamaño de fuente de interfaz), Atajos de teclado (reasignables, detección de conflictos), Notas diarias, Plantillas, **Cuentas** (GitHub), **Sincronización** (por vault), Acerca de (versión, logs, licencias).

## F-015 Sincronización con GitHub

Ver especificación completa en [06](06-sincronizacion-github.md). Escenarios de aceptación de UI:

```gherkin
Escenario: F-015.1 Iniciar sesión con GitHub
  Cuando pulso "Añadir cuenta de GitHub"
  Entonces veo un código de 8 caracteres, un botón "Copiar y abrir github.com/login/device"
  Y tras autorizar en el navegador, la cuenta aparece con su avatar y login sin más acciones

Escenario: F-015.2 Configurar sincronización creando repositorio
  Dado un vault local "Notas" sin sincronización
  Cuando elijo cuenta "boris", "Crear repositorio nuevo", nombre "notas-vault", privado
  Entonces se crea el repo, se sube el contenido en un commit inicial y el estado pasa a "Sincronizado"

Escenario: F-015.3 Clonar vault desde GitHub
  Cuando en el selector de vaults elijo "Clonar desde GitHub" y un repo existente
  Entonces se crea el vault local, se descargan todos los ficheros y se abre

Escenario: F-015.4 Indicador de estado
  Entonces la barra de estado muestra por vault: icono + texto ("Sincronizado hace 2 min", "3 cambios pendientes", "Sincronizando…", "Sin conexión", "Conflictos (2)", "Error: requiere volver a iniciar sesión")

Escenario: F-015.5 Políticas distintas por vault
  Dado el vault "Personal" con sync automática cada 5 min y el vault "Trabajo" con sync manual en otra cuenta
  Entonces "Personal" se sincroniza solo y "Trabajo" solo cuando pulso "Sincronizar ahora"
  Y un error en "Trabajo" no afecta al estado de "Personal"

Escenario: F-015.6 Resolver conflicto
  Dado un conflicto en "Ideas.md"
  Cuando abro el centro de conflictos
  Entonces veo un diff lado a lado (mío / remoto / resultado) y puedo elegir por bloque, editar el resultado y confirmar
  Y al confirmar desaparecen los marcadores/copia y se sincroniza la resolución

Escenario: F-015.7 Historial de una nota
  Cuando abro "Historial de versiones" de una nota sincronizada
  Entonces veo los commits que la modificaron (fecha, dispositivo, mensaje), puedo ver el diff con la versión actual y restaurarla
```

## F-016 Atajos de teclado por defecto

| Acción | Windows | macOS |
|--------|---------|-------|
| Quick switcher | Ctrl+O | Cmd+O |
| Paleta de comandos | Ctrl+P | Cmd+P |
| Nueva nota | Ctrl+N | Cmd+N |
| Buscar en vault | Ctrl+Shift+F | Cmd+Shift+F |
| Buscar en nota | Ctrl+F | Cmd+F |
| Alternar lectura/edición | Ctrl+E | Cmd+E |
| Cerrar pestaña | Ctrl+W | Cmd+W |
| Atrás / Adelante | Alt+← / Alt+→ | Cmd+[ / Cmd+] |
| Nota diaria | Ctrl+Shift+D | Cmd+Shift+D |
| Sincronizar vault | Ctrl+Shift+S | Cmd+Shift+S |
| Ajustes | Ctrl+, | Cmd+, |
| Negrita / Cursiva | Ctrl+B / Ctrl+I | Cmd+B / Cmd+I |
| Insertar enlace | Ctrl+K | Cmd+K |
| Alternar tarea | Ctrl+Enter | Cmd+Enter |

En iPad con teclado físico se aplican los atajos de macOS.
