"""Source of the UI texts: generates src/Basalt.Presentation/Resources/Strings.resx (en) and Strings.es.resx (es).

Edit the table, then run: python build/strings.py src/Basalt.Presentation/Resources
"""
import sys
from xml.sax.saxutils import escape

STRINGS = {
    # Common
    "Common.OK": ("OK", "Aceptar"),
    "Common.Cancel": ("Cancel", "Cancelar"),
    "Common.Close": ("Close", "Cerrar"),
    "Common.Retry": ("Retry", "Reintentar"),
    "Common.Yes": ("Yes", "Sí"),
    "Common.No": ("No", "No"),
    "Common.Busy": ("Working…", "Trabajando…"),
    "Common.ItemsCount": ("{0:N0} items", "{0:N0} elementos"),
    # Shell and vault picker (F-001)
    "Shell.Title": ("Basalt", "Basalt"),
    "Shell.TitleWithVault": ("{0} — Basalt", "{0} — Basalt"),
    "VaultPicker.Title": ("Vaults", "Vaults"),
    "VaultPicker.Create": ("Create vault", "Crear vault"),
    "VaultPicker.NameLabel": ("Vault name", "Nombre del vault"),
    "VaultPicker.ChooseLocation": ("Choose where to create the vault", "Elige dónde crear el vault"),
    "VaultPicker.OpenFolder": ("Open folder as vault", "Abrir carpeta como vault"),
    "VaultPicker.Remove": ("Remove from list", "Quitar de la lista"),
    "VaultPicker.RemoveMessage": ("“{0}” will be removed from the list. Its files are not deleted.", "«{0}» se quitará de la lista. Sus ficheros no se borran."),
    "VaultPicker.MissingTitle": ("Vault not found", "Vault no encontrado"),
    "VaultPicker.MissingMessage": ("The folder of “{0}” cannot be found: {1}", "No se encuentra la carpeta de «{0}»: {1}"),
    "VaultPicker.Locate": ("Locate…", "Localizar…"),
    "VaultPicker.LocateTitle": ("Locate the folder of “{0}”", "Localiza la carpeta de «{0}»"),
    "VaultPicker.Open": ("Open", "Abrir"),
    "VaultPicker.Empty": ("No vaults yet. Create one or open a folder.", "Aún no hay vaults. Crea uno o abre una carpeta."),
    # Menus
    "Menu.File": ("File", "Archivo"),
    "Menu.NewWindow": ("New window", "Nueva ventana"),
    "Menu.CloseVault": ("Close vault", "Cerrar vault"),
    "Menu.View": ("View", "Ver"),
    "Menu.CommandPalette": ("Command palette…", "Paleta de comandos…"),
    "Menu.QuickSwitcher": ("Quick switcher…", "Selector rápido…"),
    # Workspace chrome
    "Workspace.ToggleLeftSidebar": ("Toggle left sidebar", "Mostrar u ocultar la barra izquierda"),
    "Workspace.ToggleRightSidebar": ("Toggle right sidebar", "Mostrar u ocultar la barra derecha"),
    "Workspace.CloseTab": ("Close tab", "Cerrar pestaña"),
    "Workspace.Unsaved": ("Unsaved changes", "Cambios sin guardar"),
    "Workspace.SplitRight": ("Split right", "Dividir a la derecha"),
    "Workspace.SplitDown": ("Split down", "Dividir hacia abajo"),
    "Workspace.NoFile": ("No file open", "Ningún fichero abierto"),
    "Panel.files": ("Files", "Ficheros"),
    "Panel.search": ("Search", "Buscar"),
    "Panel.bookmarks": ("Bookmarks", "Marcadores"),
    "Panel.tags": ("Tags", "Etiquetas"),
    "Panel.backlinks": ("Backlinks", "Enlaces entrantes"),
    "Panel.outgoing": ("Outgoing links", "Enlaces salientes"),
    "Panel.outline": ("Outline", "Esquema"),
    "Panel.local-graph": ("Local graph", "Grafo local"),
    "Panel.properties": ("Properties", "Propiedades"),
    # Note tab (F-003.3)
    "Conflict.Message": ("This note changed on disk and the changes conflict with your edits.", "La nota cambió en el disco y los cambios chocan con tus ediciones."),
    "Conflict.KeepMine": ("Keep mine", "Mantener el mío"),
    "Conflict.UseDisk": ("Use the disk version", "Usar el de disco"),
    "Conflict.ShowDiff": ("Show differences", "Ver diferencias"),
    # Panels
    "Explorer.NewFolder": ("New folder", "Nueva carpeta"),
    "Explorer.FolderName": ("Folder name", "Nombre de la carpeta"),
    "Explorer.Rename": ("Rename", "Renombrar"),
    "Explorer.NewName": ("New name", "Nuevo nombre"),
    "Explorer.Delete": ("Delete", "Eliminar"),
    "Explorer.DeleteMessage": ("“{0}” will be moved to the vault trash.", "«{0}» se moverá a la papelera del vault."),
    "Bookmarks.Invalid": ("The bookmarks file could not be read; it will be kept aside when you change a bookmark.", "No se pudo leer el fichero de marcadores; se apartará cuando cambies un marcador."),
    "Properties.Invalid": ("The properties are not valid YAML; edit them in source mode.", "Las propiedades no son YAML válido; edítalas en el modo código."),
    # Settings (F-014)
    "Settings.General": ("General", "General"),
    "Settings.Editor": ("Editor", "Editor"),
    "Settings.FilesAndLinks": ("Files and links", "Ficheros y enlaces"),
    "Settings.Appearance": ("Appearance", "Apariencia"),
    "Settings.Hotkeys": ("Hotkeys", "Atajos de teclado"),
    "Settings.DailyNotes": ("Daily notes", "Notas diarias"),
    "Settings.Templates": ("Templates", "Plantillas"),
    "Settings.About": ("About", "Acerca de"),
    # Commands
    "Commands.NewNote": ("New note", "Nueva nota"),
    "Commands.OpenDailyNote": ("Open today's daily note", "Abrir la nota diaria de hoy"),
    "Commands.BookmarkActiveFile": ("Bookmark current file", "Añadir el fichero actual a marcadores"),
    # Errors (domain)
    "Errors.result.uninitialized": ("Internal error: the result was not initialized.", "Error interno: el resultado no se inicializó."),
    "Errors.path.outside_vault": ("The path is outside the vault.", "La ruta está fuera del vault."),
    "Errors.path.invalid_segment": ("The name contains characters that are not allowed.", "El nombre contiene caracteres no permitidos."),
    "Errors.vault.invalid_name": ("The vault name is not valid.", "El nombre del vault no es válido."),
    "Errors.vault.invalid_id": ("The vault identifier is not valid.", "El identificador del vault no es válido."),
    "Errors.heading.invalid_level": ("Headings go from level 1 to 6.", "Los encabezados van del nivel 1 al 6."),
    "Errors.block.invalid_id": ("The block id is not valid.", "El id de bloque no es válido."),
    "Errors.tag.invalid": ("The tag is not valid.", "La etiqueta no es válida."),
    "Errors.daily_note.invalid_format": ("The daily note format is not valid.", "El formato de nota diaria no es válido."),
    "Errors.sha.invalid": ("Invalid content hash.", "Hash de contenido no válido."),
    "Errors.account.invalid_host": ("The GitHub host is not valid.", "El servidor de GitHub no es válido."),
    "Errors.search.invalid_regex": ("The regular expression is not valid.", "La expresión regular no es válida."),
    "Errors.search.too_complex": ("The query is too complex.", "La consulta es demasiado compleja."),
    # Errors (application and adapters)
    "Errors.fs.not_found": ("The file does not exist.", "El fichero no existe."),
    "Errors.fs.already_exists": ("The file already exists.", "El fichero ya existe."),
    "Errors.fs.is_directory": ("It is a folder, not a file.", "Es una carpeta, no un fichero."),
    "Errors.fs.not_a_directory": ("It is a file, not a folder.", "Es un fichero, no una carpeta."),
    "Errors.fs.concurrent_modification": ("The file changed on disk meanwhile.", "El fichero cambió en el disco mientras tanto."),
    "Errors.fs.access_denied": ("Access denied.", "Acceso denegado."),
    "Errors.fs.io": ("The file could not be read or written.", "No se pudo leer o escribir el fichero."),
    "Errors.fs.trash_unavailable": ("This system has no recycle bin.", "Este sistema no tiene papelera de reciclaje."),
    "Errors.storage.io": ("The settings could not be saved.", "No se pudieron guardar los ajustes."),
    "Errors.path.already_exists": ("“{path}” already exists.", "Ya existe «{path}»."),
    "Errors.entry.not_found": ("“{path}” does not exist.", "No existe «{path}»."),
    "Errors.note.modified_externally": ("The note changed on disk while you were editing it.", "La nota cambió en el disco mientras la editabas."),
    "Errors.note.not_a_task": ("That line is not a task.", "Esa línea no es una tarea."),
    "Errors.note.frontmatter_invalid": ("The properties (frontmatter) are not valid YAML; fix them in the source editor.", "Las propiedades (frontmatter) no son YAML válido; corrígelas en el editor de código."),
    "Errors.entry.unsupported": ("This kind of file cannot be opened here.", "Este tipo de fichero no se puede abrir aquí."),
    "Errors.entry.move_into_itself": ("A folder cannot be moved into itself.", "Una carpeta no se puede mover dentro de sí misma."),
    "Errors.mention.not_found": ("The mention is no longer there.", "La mención ya no está."),
    "Errors.block.not_found": ("There is no block on that line.", "No hay ningún bloque en esa línea."),
    "Errors.bookmark.not_found": ("The bookmark no longer exists.", "El marcador ya no existe."),
    "Errors.command.not_found": ("Unknown command.", "Comando desconocido."),
    "Errors.command.unavailable": ("The command is not available right now.", "El comando no está disponible ahora."),
    "Errors.gesture.invalid": ("“{gesture}” is not a valid shortcut.", "«{gesture}» no es un atajo válido."),
    "Errors.operation.cancelled": ("Cancelled.", "Cancelado."),
    "Errors.vault.already_registered": ("The vault is already in the list.", "El vault ya está en la lista."),
    "Errors.vault.not_registered": ("The vault is not in the list.", "El vault no está en la lista."),
    "Errors.vault.not_open": ("The vault is not open.", "El vault no está abierto."),
    "Errors.vault.already_open": ("The vault is already open.", "El vault ya está abierto."),
    "Errors.vault.location_exists": ("The folder already exists.", "La carpeta ya existe."),
    "Errors.vault.location_missing": ("The vault folder cannot be found.", "No se encuentra la carpeta del vault."),
    "Errors.vault.id_mismatch": ("That folder belongs to another vault.", "Esa carpeta pertenece a otro vault."),
    "Errors.vault.config_invalid": ("The vault settings file is not valid.", "El fichero de ajustes del vault no es válido."),
    "Errors.account.not_found": ("The account does not exist.", "La cuenta no existe."),
    "Errors.frontmatter.too_large": ("The properties block is too large.", "El bloque de propiedades es demasiado grande."),
    "Errors.frontmatter.invalid_yaml": ("The properties are not valid YAML.", "Las propiedades no son YAML válido."),
    "Errors.frontmatter.not_a_mapping": ("The properties must be key: value pairs.", "Las propiedades deben ser pares clave: valor."),
}

HEADER = """<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
"""


def write(path, index):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(HEADER)
        for key in sorted(STRINGS):
            f.write('  <data name="%s" xml:space="preserve"><value>%s</value></data>\n' % (escape(key), escape(STRINGS[key][index])))
        f.write("</root>\n")


folder = sys.argv[1]
write(folder + "/Strings.resx", 0)
write(folder + "/Strings.es.resx", 1)
print(len(STRINGS), "keys")
