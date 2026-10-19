# 0002 — Sincronización vía API Git Data de GitHub, sin Git local

- Estado: Aceptado
- Fecha: 2026-10-19

## Contexto
Cada vault se sincroniza con su propio repositorio de GitHub. Opciones: (a) repositorio Git local (`.git`) con libgit2/LibGit2Sharp o el binario `git`; (b) API REST de GitHub (Git Data API) operando sobre objetos remotos, con estado local propio.

## Decisión
Opción (b): sin `.git` local. Estado local = base por fichero (manifiesto de blob SHAs) + caché de hashes. Fusión de tres vías por fichero con la base como ancestro. Publicación con create-tree/commit/update-ref fast-forward.

## Consecuencias
- ✅ Funciona idéntico en iOS (sin binarios nativos, sin procesos hijo; LibGit2Sharp no tiene soporte oficial para iOS).
- ✅ Sin duplicar el tamaño del vault en disco (no hay `.git/objects`), importante en móvil.
- ✅ Pruebas deterministas con un servidor GitHub falso que reproduce la semántica.
- ✅ Tolerante a historial reescrito: no depende del grafo de commits.
- ⚠️ Limitado a GitHub (y GitHub Enterprise Server); otros proveedores requerirían otro adaptador de `IRemoteRepository` (posible gracias al puerto).
- ⚠️ Sujeto a límites de tasa de la API: mitigado con contenido inline en create-tree, zipball para descargas masivas, ETag en refs y token bucket.
- ⚠️ Sin historial local offline: el historial de versiones requiere red.

## Alternativas consideradas
- LibGit2Sharp: problemas de distribución nativa en iOS y macOS universal; duplicación de almacenamiento.
- Binario `git`: no disponible en iOS.
- API Contents de GitHub (un commit por fichero): demasiados commits y peticiones; sin atomicidad multi-fichero.
