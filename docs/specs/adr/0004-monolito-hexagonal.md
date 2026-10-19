# 0004 — Monolito hexagonal modular

- Estado: Aceptado
- Fecha: 2026-10-19

## Contexto
Aplicación cliente de un solo proceso con tres plataformas destino, lógica compleja (enlaces, búsqueda, sincronización) y un requisito fuerte de pruebas.

## Decisión
Monolito con arquitectura hexagonal: Dominio puro + Aplicación (casos de uso y puertos) + adaptadores en proyectos separados + cabeceras de plataforma como raíces de composición. Módulos funcionales por carpeta dentro del núcleo con dependencias verificadas por tests de arquitectura. Casos de uso con handlers propios (sin MediatR) y `Result<T>`.

## Consecuencias
- ✅ El núcleo se prueba sin UI, disco ni red, rápido y determinista.
- ✅ Adaptadores intercambiables (p. ej. otro proveedor Git, otro índice) sin tocar el núcleo.
- ✅ Una sola unidad de despliegue por plataforma.
- ⚠️ Más proyectos y algo más de ceremonia (puertos + fakes + contratos); compensado por la calidad de las pruebas.
