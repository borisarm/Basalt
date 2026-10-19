# 00 — Visión y alcance

## 1. Visión

Basalt es una aplicación de toma de notas en Markdown, local-first, con enlaces bidireccionales y grafo de conocimiento, funcionalmente equivalente al núcleo de Obsidian, que se ejecuta de forma nativa en **Windows, macOS e iOS** con una única base de código **.NET 10 + Avalonia**.

Su diferencial es la **sincronización por vault sobre GitHub**: cada vault se respalda y sincroniza contra *su propio* repositorio de GitHub, con historial completo de versiones, sin servidores propios y con configuración independiente por vault (cuenta, repositorio, rama, frecuencia, política de conflictos, exclusiones).

## 2. Usuarios objetivo

| Perfil | Necesidad principal |
|--------|---------------------|
| Escritor/investigador personal | Notas enlazadas, búsqueda rápida, disponibles en el móvil |
| Desarrollador | Notas técnicas versionadas en Git, legibles desde GitHub |
| Usuario con varios contextos | Vault personal en una cuenta, vault de trabajo en otra (cuenta/organización distinta), cada uno con su política |

## 3. Objetivos medibles

- O1. Abrir un vault de 10 000 notas y poder editar en < 2 s en un equipo de referencia (ver [11](11-requisitos-no-funcionales.md)).
- O2. Sincronizar un cambio de una nota en < 5 s (red normal) de extremo a extremo.
- O3. Cero pérdida de datos en la batería de simulación multi-dispositivo (≥ 10 000 escenarios aleatorios en CI nocturno).
- O4. Cobertura de líneas ≥ 95 % en Dominio, ≥ 90 % en Aplicación; puntuación de mutación ≥ 80 % en Dominio.
- O5. Un vault de Basalt DEBE poder abrirse sin cambios en Obsidian y viceversa (compatibilidad de formato de notas).

## 4. Alcance

### 4.1 MVP (fases 0–9 del plan)

- Gestión de múltiples vaults (crear, abrir, cerrar, eliminar del registro).
- Explorador de ficheros: árbol de carpetas, crear/renombrar/mover/eliminar notas y carpetas, papelera `.trash/`.
- Editor Markdown (modo fuente con resaltado) y modo lectura (renderizado).
- Wikilinks `[[...]]`, enlaces Markdown, embebidos `![[...]]`, alias, encabezados y bloques (`#`, `^`).
- Autocompletado de enlaces y etiquetas.
- Actualización automática de enlaces al renombrar/mover.
- Panel de backlinks (enlazadas y menciones no enlazadas) y enlaces salientes.
- Etiquetas (incluidas anidadas `#a/b`) y panel de etiquetas.
- Frontmatter YAML / panel de propiedades.
- Búsqueda de texto completo con operadores (`path:`, `file:`, `tag:`, `line:`, `section:`, `-`, `OR`, comillas, regex `/.../`).
- Quick switcher (búsqueda difusa de notas) y paleta de comandos.
- Notas diarias y plantillas.
- Esquema (outline) de la nota.
- Marcadores (bookmarks).
- Vista de grafo global y local.
- Adjuntos: pegar/arrastrar imágenes, visualización de imágenes embebidas.
- Pestañas y paneles divididos (escritorio).
- Ajustes: apariencia (claro/oscuro/sistema), tipografía, editor, atajos de teclado.
- **Sincronización por vault con GitHub**: autenticación OAuth Device Flow, múltiples cuentas, crear repo o vincular existente, clonar vault desde repo, sincronización manual/automática, resolución de conflictos, historial de versiones de una nota, registro de sincronización.

### 4.2 v1 (fases 10–11)

- Aplicación iOS completa (iPhone y iPad) con las mismas capacidades salvo paneles divididos en iPhone.
- Sincronización en segundo plano en iOS (best-effort mediante `BGTaskScheduler`).
- Empaquetado y firma para las tres plataformas.
- Pulido de accesibilidad y rendimiento.

### 4.3 Fuera de alcance (explícitamente)

- Ecosistema de plugins de terceros y API de plugins JavaScript. (La arquitectura DEBE dejar puntos de extensión internos — comandos, vistas — pero no se expone API pública.)
- Canvas (`.canvas`), Publish, temas CSS de la comunidad.
- Edición colaborativa en tiempo real.
- Android y Linux (no se prohíbe; no se prueba ni empaqueta).
- Cifrado de extremo a extremo del contenido en el repositorio (se documenta como posible extensión futura, ver ADR-0005).
- Git LFS.
- Live Preview completo estilo Obsidian (ocultar sintaxis en línea mientras se edita). En MVP hay modo fuente con resaltado + modo lectura; el modo "Live preview" es una mejora posterior (fase 11, opcional).

## 5. Glosario

| Término | Definición |
|---------|------------|
| **Vault** | Carpeta raíz que contiene notas, adjuntos y la carpeta de configuración `.basalt/`. |
| **Nota** | Fichero `.md` dentro de un vault. |
| **Adjunto** | Cualquier fichero que no es `.md` (imágenes, PDF, etc.). |
| **Wikilink** | Enlace con sintaxis `[[destino#ancla\|alias]]`. |
| **Embed** | Inclusión de contenido con `![[destino]]`. |
| **Backlink** | Enlace entrante: otra nota que enlaza a la nota actual. |
| **Mención no enlazada** | Aparición del título de la nota en texto plano de otra nota, sin enlace. |
| **Frontmatter** | Bloque YAML al inicio de la nota delimitado por `---`. |
| **Perfil de sincronización** | Configuración de sincronización de un vault concreto (cuenta, repo, rama, política…). |
| **Base de sincronización** | Instantánea (commit SHA + manifiesto de ficheros con sus blob SHA) del último estado sincronizado con éxito; ancestro común para la fusión de tres vías. |
| **Manifiesto** | Mapa `ruta → blob SHA (+tamaño)` de un estado del vault. |
| **Plan de sincronización** | Lista de acciones calculada de forma pura a partir de (base, local, remoto). |
| **Copia de conflicto** | Fichero creado junto al original cuando no se puede fusionar automáticamente: `Nota (conflict YYYY-MM-DD HHmmss Dispositivo).md`. |
| **Dispositivo** | Instalación concreta de Basalt; tiene un nombre legible y un `DeviceId` estable. |
| **Puerto** | Interfaz definida en Aplicación que abstrae una capacidad (entrada: casos de uso; salida: E/S). |
| **Adaptador** | Implementación concreta de un puerto (ficheros, GitHub, SQLite, Avalonia…). |
