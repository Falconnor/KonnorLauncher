# Glass Browse — Distribución y diseño

![Referencia visual](./referencia-glass.png)

Página de exploración de contenido de Minecraft en estilo glassmorphism: fondo oscuro con manchas de color difuminadas y paneles de vidrio translúcido encima.

---

## 1. Distribución

```
┌──────────────────────────────────────────────────────────────┬─────────────────┐
│ [Modpacks][Mods][Resource Packs][Data Packs][Shaders][Servers]│  SIDEBAR        │
│ ┌──────────────────────────────────────────────────────────┐ │  Categoría   ^  │
│ │ 🔍 Buscar modpacks…                                  [/] │ │  ☐ Aventura 4.1k│
│ └──────────────────────────────────────────────────────────┘ │  ☑ Optimización │
│ [Ordenar ▾][Ver: 20 ▾] (chip×)(chip×)          (1) 2 3 … 956 ›│  ☒ Misiones     │
│ ┌──────────────────────────────────────────────────────────┐ │  Contenido inc. │
│ │ ICON │ Título  por Autor                   [ + Instalar ]│ │  Entorno        │
│ │ 92px │ Descripción…                       ⬇ 19.7M ♡ 5,030│ │  Versión juego  │
│ │      │ (tag)(tag)(tag)(Fabric)            ⟲ hace 12 h   │ │  Loader         │
│ └──────────────────────────────────────────────────────────┘ │  Licencia    v  │
│ ┌ tarjeta ─────────────────────────────────────────────────┐ │                 │
│ └──────────────────────────────────────────────────────────┘ │[Limpiar filtros]│
└──────────────────────────────────────────────────────────────┴─────────────────┘
```

- Contenedor: `max-width:1600px`, `padding:28px 32px`, centrado.
- Dos columnas: contenido `1fr` + sidebar `330px`, separación `24px`.
- Columna principal, de arriba a abajo (separación 14–16px):
  1. Tabs de tipo (Modpacks, Mods, Resource Packs, Data Packs, Shaders, Servers).
  2. Barra de búsqueda a todo el ancho.
  3. Toolbar: ordenar, cantidad por página, chips de filtros activos y paginación alineada a la derecha.
  4. Lista vertical de tarjetas (separación 14px).
- Sidebar fijo (`sticky`) con grupos en este orden: Categoría, Contenido incluido, Entorno, Versión del juego, Loader, Licencia y botón "Limpiar filtros".
- Todo alineado a la izquierda.

### Responsive

| Ancho | Distribución |
|---|---|
| ≥ 1280px | Dos columnas como la referencia |
| 1024–1279px | Sidebar de 290px |
| 768–1023px | Sidebar oculto; aparece botón "Filtros" que abre un panel lateral derecho |
| < 768px | Tabs con scroll horizontal; tarjeta en dos filas (icono 64px + título arriba, stats + botón abajo); filtros en panel inferior |

---

## 2. Diseño

### Colores

| Token | Valor | Uso |
|---|---|---|
| `--bg` | `#0A1230` | Fondo (azul medianoche) |
| `--brand` | `#1BD96A` | Verde principal: tab activo, botones, selección |
| `--brand-ink` | `#04210F` | Texto sobre verde |
| `--violet` | `#8B5CF6` | Mancha de fondo |
| `--cyan` | `#22D3EE` | Mancha de fondo |
| `--rose` | `#F472B6` | Mancha de fondo |
| `--text` | `#EEF2FF` | Texto principal |
| `--text-2` | `#B4BCD8` | Texto secundario |
| `--text-3` | `#7F88A8` | Texto terciario, contadores |

Colores de loader en tags/pills:

| Loader | Texto | Borde | Fondo |
|---|---|---|---|
| Fabric | `#F5D9A8` | `rgba(251,191,36,.30)` | `rgba(251,191,36,.08)` |
| Forge | `#C7C2FF` | `rgba(139,92,246,.35)` | `rgba(139,92,246,.10)` |
| NeoForge | `#FFC1A1` | `rgba(251,146,60,.35)` | `rgba(251,146,60,.10)` |

### Tipografía

Fuente **Outfit** (Google Fonts), sin mayúsculas en etiquetas.

| Elemento | Tamaño | Peso |
|---|---|---|
| Título de tarjeta | 21px | 700 |
| Encabezado de grupo del sidebar | 15px | 700 |
| Tabs y botones | 14–15px | 600 |
| Descripción | 15px, interlineado 1.45 | 400 |
| Tags y contadores | 12.5–13px | 400–500 |

### Fondo

Fondo `--bg` con 4 manchas circulares difuminadas (`filter: blur(90px)`):

| Mancha | Tamaño | Color | Posición | Opacidad |
|---|---|---|---|---|
| 1 | 620px | verde | arriba a la izquierda | .45 |
| 2 | 700px | violeta | derecha, a media altura | .55 |
| 3 | 520px | cian | abajo al centro | .40 |
| 4 | 300px | rosa | arriba al centro | .30 |

Encima, una textura de grano muy suave (puntos de 1px cada 3px, opacidad .07).

### Superficie de vidrio

Se usa en tabs, búsqueda, selects, paginación, tarjetas y sidebar:

```css
.glass{
  background: rgba(255,255,255,.065);
  backdrop-filter: blur(22px) saturate(160%);
  border: 1px solid rgba(255,255,255,.14);
  box-shadow: inset 0 1px 0 rgba(255,255,255,.18),   /* brillo en el borde superior */
              0 12px 40px -12px rgba(2,6,23,.55);
}
```

Los elementos dentro de un panel (tags, pills, inputs del sidebar) no llevan blur: solo `background: rgba(255,255,255,.05)` y borde `rgba(255,255,255,.08)`.

### Radios

| Elemento | Radio |
|---|---|
| Tarjetas y sidebar | 22px |
| Búsqueda, icono de tarjeta | 16–20px |
| Selects, opciones del sidebar | 10–12px |
| Tabs, botones, tags, pills, paginación | completamente redondeados |

---

## 3. Componentes

**Tabs de tipo** — contenedor de vidrio redondeado con 6px de relleno. Cada tab lleva icono a la izquierda y texto `--text-2`. El activo es una pastilla con degradado verde, texto `--brand-ink` y brillo verde exterior (`0 0 24px -4px rgba(27,217,106,.7)`).

**Búsqueda** — vidrio, relleno 15px 20px, icono de lupa en `--text-3`, placeholder 16px y una tecla `/` a la derecha como pista.

**Toolbar**
- Selects de vidrio con el formato "Ordenar por: **Relevancia** ▾" y "Ver: **20** ▾".
- Chips de filtros activos en verde translúcido (`rgba(27,217,106,.14)`, borde `rgba(27,217,106,.4)`) con una ✕.
- Paginación en una pastilla de vidrio con botones circulares de 34px; la página actual va en verde sólido.

**Tarjeta**
- Tres columnas: icono de 92px, contenido y acciones.
  - **Icono:** cuadrado con radio de 20px y borde fino.
  - **Contenido:** título con "por autor" en gris al lado, descripción de máximo 2 líneas y fila de tags. Los tags van en este orden: entorno, categorías, loader con su color y "+N" si sobran.
  - **Acciones:** columna alineada a la derecha con el botón "Instalar" (contorno verde translúcido), las descargas y favoritos, y debajo la fecha de actualización en `--text-3`.
- **Hover:** el fondo se aclara a `rgba(255,255,255,.12)`, el borde se aclara y el botón pasa a verde sólido con brillo.

**Sidebar**
- Panel de vidrio con grupos plegables separados por una línea fina.
- El encabezado de cada grupo lleva título en negrita, flecha y "N activo" en verde si aplica.
- **Categoría:** filas con checkbox de 16px, icono, nombre y contador a la derecha.
  - Incluida: check verde y fondo verde translúcido.
  - Excluida: ✕ roja y texto `#FCA5A5`.
- **Contenido incluido:** input pequeño de búsqueda.
- **Entorno, Versión del juego y Loader:** pills. La activa va en verde translúcido (`rgba(27,217,106,.16)`, borde `rgba(27,217,106,.5)`).
- **Versión del juego:** suma un interruptor "Mostrar snapshots" de 36×20.
- **Licencia:** grupo plegado.
- **"Limpiar filtros":** botón con borde discontinuo al final.
