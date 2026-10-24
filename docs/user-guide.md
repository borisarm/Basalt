# Guía de usuario de Basalt

Basalt es una aplicación de notas Markdown local-first para Windows, macOS e iOS. Los vaults son
carpetas normales de ficheros `.md` compatibles con Obsidian, y cada vault puede sincronizarse con
su propio repositorio de GitHub.

> Esta guía describe el uso previsto del MVP. Basalt está en desarrollo temprano; consulta
> [`PROGRESS.md`](../PROGRESS.md) para ver el estado real de cada función.

## 1. Instalar y abrir

- **Windows**: ejecuta el instalador MSIX o descomprime el ZIP portable y abre `Basalt.exe`.
- **macOS**: abre el `.dmg`, arrastra `Basalt.app` a Aplicaciones y ábrelo.
- **iOS**: instala desde TestFlight o el App Store. Los vaults aparecen en la app Archivos.

Al arrancar sin ningún vault verás el selector de vaults.

## 2. Crear o abrir un vault

1. Pulsa **Crear vault**.
2. Elige un nombre y una carpeta. Basalt crea una carpeta con ese nombre y dentro `.basalt/vault.json`.
3. También puedes usar **Abrir carpeta como vault** con una carpeta que ya contenga notas Markdown.
   Basalt añade `.basalt/vault.json` y no modifica el contenido existente.
4. La lista permite **Localizar** un vault cuya carpeta se movió y **Quitar de la lista**. Quitar un
   vault nunca borra sus ficheros.

En escritorio puedes tener varios vaults abiertos, cada uno en su ventana y con su propia
sincronización. En iOS solo hay un vault abierto a la vez.

## 3. La ventana de trabajo

- **Barra lateral izquierda**: explorador de ficheros, buscar, marcadores, etiquetas y grafo global.
- **Editor central**: pestañas, división horizontal o vertical y barra de estado.
- **Barra lateral derecha**: backlinks, menciones sin enlazar, enlaces salientes, esquema,
  propiedades y grafo local.
- **Barra de estado**: estado de sincronización del vault, conflictos y acciones rápidas.

Los paneles se muestran u ocultan con los botones de la barra superior. Arrastra una pestaña para
moverla entre grupos o usa el menú para dividir el área de edición.

## 4. Notas y editor

- Crea una nota con **Ctrl+N** (`Cmd+N` en macOS). El nombre se propone a partir del título y se
  evita duplicar nombres existentes.
- El editor guarda automáticamente como máximo 2 s después de la última pulsación y al cerrar.
- **Ctrl+E** alterna entre edición y lectura.
- La sintaxis Markdown se resalta en modo edición; la vista de lectura muestra el documento
  renderizado sin ejecutar HTML ni JavaScript.
- En iOS la barra de accesorios inserta `[[`, encabezados, tareas, y permite deshacer, rehacer,
  sangrar y ocultar el teclado.

### Enlaces y renombrado

- Escribe `[[Nota]]` para un wikilink. La autocompletación sugiere notas mientras escribes.
- Los enlaces admiten alias (`[[Nota|texto]]`), encabezados (`[[Nota#Sección]]`) y bloques.
- `![[Nota]]` incrusta una nota y `![[imagen.png]]` muestra un adjunto.
- Al renombrar o mover una nota, Basalt actualiza los enlaces que apuntan a ella si la opción
  **Actualizar enlaces al renombrar** está activa.
- Los enlaces sin destino se muestran como *sin resolver* y aparecen en el panel de menciones.

## 5. Buscar, atajos y herramientas

- **Ctrl+O** (`Cmd+O`): quick switcher.
- **Ctrl+P** (`Cmd+P`): paleta de comandos, con todas las acciones disponibles y sus atajos.
- **Ctrl+Shift+F** (`Cmd+Shift+F`): búsqueda en el vault. Admite `path:`, `file:`, `tag:`, `line:`,
  `section:`, `-` para excluir, `OR`, comillas y `/regex/`.
- **Ctrl+F** (`Cmd+F`): buscar dentro de la nota.
- **Ctrl+Shift+D** (`Cmd+Shift+D`): abrir la nota diaria.
- **Ctrl+,** (`Cmd+,`): ajustes.

Consulta la tabla completa de atajos en
[`docs/specs/04-especificacion-funcional.md`](specs/04-especificacion-funcional.md#f-016-atajos-de-teclado-por-defecto).

## 6. Sincronizar un vault con GitHub

Cada vault se sincroniza con **un repositorio y una rama**. Basalt usa la API de Git de GitHub
directamente: no crea una carpeta `.git` en tu vault, así que el mismo vault funciona igual en
Windows, macOS e iOS.

### Conectar una cuenta

1. Abre **Ajustes → Cuentas** y pulsa **Añadir cuenta**.
2. Aparece un código de dispositivo. Pulsa **Copiar y abrir github.com/login/device**, introduce el
   código y autoriza la aplicación.
3. La cuenta aparece con su login. El token se guarda en el almacén seguro del sistema operativo
   (Credential Manager en Windows, Keychain en macOS/iOS), nunca en la carpeta del vault ni en los
   logs.

### Configurar el vault

1. Abre **Ajustes → Sincronización** y pulsa **Configurar**.
2. Elige la cuenta, el propietario y el nombre del repositorio. Puedes crear uno nuevo (privado o
   público) o usar uno existente.
3. Elige el modo de unión cuando haya contenido en ambos lados: mantener ambos, preferir local o
   preferir remoto. Basalt muestra una vista previa antes de confirmar.
4. Confirma. El estado pasa a **Sincronizado** cuando termina el primer ciclo.

### Clonar un vault existente

En el selector de vaults elige **Clonar desde GitHub**, selecciona el repositorio y la carpeta local.
Basalt descarga el contenido y abre el vault con el mismo identificador que el original.

### Estado, pausa y desactivación

La barra de estado muestra el resultado del último ciclo: sincronizado, cambios pendientes,
sincronizando, sin conexión, conflictos o error de autenticación. Desde ahí puedes:

- **Sincronizar ahora** (o **Ctrl+Shift+S** / `Cmd+Shift+S`).
- **Pausar** y **Reanudar** la sincronización automática.
- **Deshabilitar** la sincronización. Desactivarla borra el perfil y el estado local de sync del
  vault, pero **nunca** borra tus notas ni el repositorio remoto.

Cada vault tiene su propia política: uno puede sincronizarse cada pocos minutos y otro solo de forma
manual, incluso con cuentas distintas. Un error en un vault no afecta a los demás.

## 7. Conflictos e historial

Basalt nunca descarta una versión sin preguntar.

- Si dos dispositivos editan el mismo fichero, Basalt intenta una fusión de tres vías. Si los cambios
  no se solapan, se combinan automáticamente.
- Si se solapan, según la política del vault se conservan ambos ficheros como **copia de conflicto**
  o se escriben marcadores de fusión en el fichero original.
- Los conflictos aparecen en la barra de estado y en el **Centro de conflictos**. Allí puedes ver un
  diff lado a lado, elegir una resolución (**mantener el mío**, **usar el remoto**, **mantener ambos**
  o **usar el texto fusionado**), editar el resultado y confirmar.
- **Historial de versiones** de una nota muestra los commits que la modificaron con fecha,
  dispositivo y mensaje. Puedes comparar con la versión actual y restaurar una versión anterior; la
  restauración se guarda como una edición local más.

El estado de sincronización y el journal de diagnóstico se guardan en `{AppData}`. Puedes copiar un
resumen de diagnóstico desde **Ajustes → Acerca de** sin incluir nombres de ficheros ni tokens.

## 8. iOS

- Usa la barra inferior para explorar, buscar, abrir la nota diaria, crear una nota o abrir el menú
  **Más**.
- El botón de la barra superior vuelve al explorador; el panel **Más** abre quick switcher, paleta,
  marcadores, etiquetas, grafo, conflictos e historial.
- Basalt guarda al entrar en segundo plano y programa una sincronización en segundo plano cuando el
  sistema lo permite. Si el sistema no concede tiempo, el siguiente ciclo se ejecuta al volver a la
  app.
- Los vaults se guardan en la carpeta Documentos de la app y son visibles en Archivos.

## 9. Privacidad y datos

- Las notas son tuyas: se guardan como ficheros Markdown en la carpeta del vault.
- No hay telemetría remota.
- Basalt solo modifica la carpeta del vault y su carpeta de datos en `{AppData}`.
- El contenido de las notas no se registra en los logs. Los tokens solo viven en el almacén seguro
  del sistema operativo.

## 10. Problemas frecuentes

| Síntoma | Qué hacer |
|---------|-----------|
| «No se encuentra la carpeta» | Usa **Localizar** en el selector de vaults. |
| «Requiere volver a iniciar sesión» | Vuelve a autorizar la cuenta en **Ajustes → Cuentas**. |
| La sincronización no avanza | Comprueba la conexión, que el vault no esté pausado y que la rama exista. |
| Aparecen conflictos | Resuélvelos en el Centro de conflictos; no borres las copias de conflicto a mano. |
| Un cambio externo no aparece | Sincroniza manualmente. Si el repositorio se reescribió, Basalt vuelve a leer la base. |
