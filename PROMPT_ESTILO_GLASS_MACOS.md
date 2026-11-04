# Prompt para Opus 5.5 (VS Code): estilo "glass de escritorio" en el launcher WPF

> **Cómo usarlo:** abre la carpeta del repo en VS Code, adjunta las dos capturas de referencia
> (la tienda de apps y el chat) al chat del agente y pega **todo lo que hay debajo de la línea**.
> Conviene ir fase por fase: pide "haz solo la Fase 1", revisa, compila y sigue.

---

## Rol y contexto

Eres un ingeniero senior de WPF (.NET 10, C# 14, XAML, CommunityToolkit.Mvvm). Vas a cambiar el
aspecto visual del launcher de Minecraft de este repositorio (`Launcher/src/KonnorLauncher.App`)
para que se parezca a las capturas adjuntas: una **única ventana de cristal oscuro y translúcido,
con esquinas muy redondeadas, sobre un fondo vivo y difuminado**, con barra lateral, pestañas
superiores con subrayado, listas agrupadas en contenedores redondeados y botones tipo "píldora".

Es un **rediseño visual**, no funcional:
- No toques `KonnorLauncher.Core` ni la lógica de los ViewModels. Solo puedes añadir propiedades de
  presentación a un ViewModel si una vista nueva las necesita de verdad (p. ej. la pestaña activa).
- Mantén todos los `Binding`, `Command`, `x:Name` usados desde code-behind, `AutomationProperties.Name`,
  atajos de teclado y textos en español.
- Todo el texto de la UI y los comentarios en español, como el resto del código.
- No añadas paquetes NuGet de temas (ni MahApps, ni WPF-UI, ni HandyControl). Solo XAML + C#.

## Lo que ya existe (léelo antes de empezar)

1. `Themes/Glass.xaml`: tokens (`Ink`, `Ink2`, `Ink3`, `GlassTint`, `GlassStroke`, `Surface*`,
   `Field*`, `Accent*`, `Ok`, `Warn`, `Danger`, `Info`), fuentes (`UiFont`, `DisplayFont`, `IconFont`
   = Segoe Fluent Icons), glifos (`IconSettings`, `IconSearch`…) y estilos (`GlassButton`,
   `AccentButton`, `QuietButton`, `IconOnlyButton`, `WindowButton`, `SegmentButton`, `GlassTextBox`,
   `GlassScrollViewer`, `InstanceListItem`, `Chip`, `GlassSlider`…).
2. `Controls/GlassPanel.cs`: panel que desenfoca lo que tiene detrás mediante `VisualBrush` +
   `BlurEffect`, usando la propiedad heredada `GlassPanel.Backdrop`. **Es la base del efecto; reutilízalo**,
   no lo sustituyas por `AllowsTransparency=True` (rompe el rendimiento y el WebView2).
3. `Themes/Colors.xaml`: paleta antigua. No la borres; puede seguir usándose en sitios concretos.
4. `Views/MainWindow.xaml`: ventana sin marco (`WindowChrome`), `Grid x:Name="Backdrop"` con
   `background.png`, y `MainContent` con dos columnas (barra lateral 292 px + zona principal).
5. `Views/BrowseView.xaml` (explorador de Modrinth) y los overlays (`*Overlay.xaml`).
6. `GLASS_BROWSE_SPEC.md` en la raíz: especificación previa del explorador. Las reglas de este prompt
   tienen prioridad si chocan.

Lee esos archivos completos antes de editar nada y respeta sus convenciones.

## El estilo de referencia, traducido a números

### Ventana
- Una sola "ventana de cristal" que ocupa la ventana WPF con un margen de **10 px** (para que se vea
  la sombra y el fondo alrededor), `CornerRadius` **18**, borde 1 px `#1FFFFFFF`, sombra suave
  (`DropShadowEffect` BlurRadius 40, Opacity 0.45, ShadowDepth 8). Al maximizar: margen 0 y radio 0.
- Dentro, **sin huecos entre paneles**: barra lateral y contenido están pegados y separados solo por
  una línea vertical de 1 px `#14FFFFFF`. La cabecera se separa del cuerpo con una línea horizontal igual.
- La barra lateral es un poco **más oscura** que el contenido (capa extra `#26000000` encima del cristal).
- Tinte del cristal: azul‑pizarra frío, no negro puro. Añade/ajusta tokens:
  - `WindowTint` `#B81B1F2C` (contenido), `SidebarTint` `#C4151925`, `HeaderTint` `#14FFFFFF`.
  - `Hairline` `#14FFFFFF` (separadores), `HairlineStrong` `#24FFFFFF`.
- `BlurRadius` del cristal de la ventana: 60–80 (más que ahora) para que el fondo sea solo color.

### Fondo (lo que da vida al cristal)
En las capturas el cristal se ve colorido porque detrás hay un fondo **muy saturado** (azul → violeta
→ rojo). Dentro de `Grid x:Name="Backdrop"`, por encima de `background.png` y debajo del `Scrim`:
- Añade una capa "aurora": 3–4 `Ellipse` grandes (600–900 px) con `RadialGradientBrush`
  (`#3A5BFF`, `#8E3BFF`, `#FF2E63`, `#1FB6FF`, de opaco a transparente) y opacidad 0.45–0.6,
  colocadas en esquinas distintas. Nada de animación por defecto (opcional: una deriva lentísima de
  60 s con `RenderTransform`, desactivable en Opciones o si `SystemParameters.ClientAreaAnimation` es false).
- Rebaja el `Scrim` si hace falta para que el color atraviese el cristal, pero comprueba el contraste
  del texto (ver Accesibilidad).

### Tipografía (equivalente Windows de SF Pro)
- `UiFont` / `DisplayFont` existentes (Segoe UI Variable). Pesos **ligeros**: la referencia casi no usa negrita.
- Título de sección grande ("All Apps", nombre de la instancia): 20–22, `Normal`/`SemiLight`.
- Pestañas superiores: 15, `Normal`. Elementos de la barra lateral: 15. Texto de filas: 17 (nombre) y 13.5 (secundario).
- Etiquetas de grupo de la barra lateral ("Apps", "Categories"): 13, `Ink3`, sin mayúsculas forzadas.
- Descripciones de tarjetas: 13.5, `Ink2`, interlineado 20.

### Barra superior (cabecera de la ventana), alto 62
De izquierda a derecha:
1. **Semáforos** (sustituyen a los `WindowButton` actuales): tres círculos de 12 px, separados 8 px,
   colores `#FF5F57` (cerrar), `#FEBC2E` (minimizar), `#28C840` (maximizar/restaurar), borde 0.5 px
   `#33000000`. Al pasar el ratón por **el grupo**, cada uno muestra su glifo (×, −, ⤢) en `#99000000`
   a 8 px. Cuando la ventana no está activa (`IsActive=false`) se vuelven `#4DFFFFFF`.
   Conserva los manejadores `OnCloseClick`, `OnMinimizeClick`, `OnMaximizeClick` y el
   `AutomationProperties.Name`. La barra lateral empieza **debajo** de ellos (están en su columna).
2. **Pestañas principales** (en la columna del contenido): "Instancias", "Explorar" y, si encaja,
   "Opciones". Texto `Ink3`, la activa `Ink`; indicador **subrayado de 2 px blanco** pegado a la línea
   inferior de la cabecera, del ancho del texto + 16 px, con transición de 150 ms. Un punto azul/verde
   de 6 px arriba a la derecha del texto indica novedades (p. ej. actualizaciones de instancia).
   Implementa con `RadioButton` + estilo nuevo `TopTab`, ligado a `IsBrowsing` / comandos existentes
   (`OpenBrowseCommand`, etc.).
3. **Búsqueda** alineada a la derecha de las pestañas: sin borde, fondo `#1F000000`, radio 8, alto 34,
   ancho 320, icono lupa `Ink3` a la izquierda, placeholder `Ink3`. Si no hay búsqueda global, que
   lleve al explorador con el foco en su caja de búsqueda.
4. A la derecha: botón campana con **insignia** (círculo 16 px azul/acento, número 10 px negrita) y
   **avatar del jugador** 36 px redondo con anillo 2 px `#33FFFFFF` (abre `OpenAccountCommand`).
   Toda la cabecera vacía sirve para arrastrar la ventana (ya lo hace `OnWindowMouseLeftButtonDown`).

### Subcabecera, alto 54
- Título de la vista a la izquierda (20, `Ink`), y pestañas secundarias centradas con el mismo estilo
  `TopTab` pero 14 px (en Explorar: Modpacks / Mods / Resource Packs / Shaders…; ya existen como
  `BrowseTabButton`: unifícalos con el nuevo estilo).

### Barra lateral (ancho 260)
- Grupos con etiqueta pequeña `Ink3` y elementos debajo; 28 px de separación entre grupos.
- Elemento: alto 38, padding 10 px horizontal, icono Fluent 15 px + texto 15 px, `Ink2`.
  Hover: fondo `#0FFFFFFF`. Seleccionado: fondo `#1AFFFFFF`, radio 8, texto `Ink`.
- **Contador** a la derecha (p. ej. instancias con actualización): círculo 18 px color acento, número 11 px.
- Para la lista de instancias usa el patrón del chat (2.ª captura): fila de 72 px, icono/avatar
  44 px redondo (o cuadrado radio 10 para iconos de modpack), nombre 15 + resumen 13 `Ink3` en una
  línea con elipsis, **punto de estado** 10 px con anillo 2 px del color del fondo en la esquina
  inferior derecha del icono (verde `Ok` = lista, ámbar `Warn` = actualización, ninguno = normal).
  Separador de 1 px `Hairline` entre filas, sin margen lateral. Seleccionada: `#14FFFFFF` a todo el ancho.
- Abajo del todo: tarjeta del jugador (avatar + nombre + "Jugador sin conexión") y "Opciones".

### Zona de contenido
- Padding 32 px horizontal, 28 px arriba. ScrollViewer con barra fina superpuesta (la existente
  `GlassScrollViewer`), con el carril pegado al borde derecho.
- **Banner destacado** (equivalente a "Adobe Stock"): rectángulo radio 14, alto 220, fondo
  `LinearGradientBrush` diagonal `#7B2FF7` → `#F107A3` → `#FF8A3D`, con 3–5 formas decorativas
  (elipses con degradado radial y sombra) a la derecha. Icono de 32 px en "tile" radio 7, título 20,
  texto 14 `#E6FFFFFF` máx. 3 líneas y ancho 380, botón píldora azul/acento. Úsalo para la instancia
  destacada, el modpack del servidor o noticias. Si no hay datos, se oculta.
- **Lista agrupada** ("Installed"): etiqueta de sección 14 `Ink3` con 12 px debajo; contenedor con
  radio 12, fondo `#0AFFFFFF`, borde 1 px `Hairline`. Filas de 58 px separadas por `Hairline`
  (la primera y la última sin separador exterior). Columnas de la fila:
  `[tile 34 px] 18px [nombre 17] * [● estado 13] * [botón] 16px [⋯]`.
  - Tile de icono: cuadrado radio 7, fondo muy oscuro y **borde 1.5 px del color de la app**
    (genera el color desde el loader: Fabric `#DBB68F`, Forge `#E0743A`, NeoForge `#F0A040`,
    Quilt `#C77DFF`, Vanilla `#7AE08A`), letras/icono de ese color.
  - Estado: punto 7 px + texto 14 `Ink2` ("Actualizada" verde, "Actualización disponible" azul/acento).
  - Botón: "Abrir"/"Jugar" como **píldora contorno** o "Actualizar" como **píldora rellena**.
  - `⋯`: `IconOnlyButton` 28 px circular con glifo `&#xE712;` que abre un `ContextMenu` con estilo de cristal.
- **Rejilla de tarjetas** ("Apps in your plan"): `UniformGrid`/`WrapPanel` de 3 columnas (2 por debajo
  de 1100 px de ancho de contenido), 24 px de separación. Tarjeta radio 12, fondo `#0AFFFFFF`, borde
  `Hairline`, padding 22: tile + título 20 en la misma línea, descripción debajo, y un **pie separado
  por una línea `Hairline`** con el botón píldora y `⋯` alineados a la derecha.
  Aplícalo a las tarjetas de resultados de `BrowseView` y a la vista de instancias vacía.

### Botones (estilos nuevos en `Glass.xaml`)
- `PillButton` (relleno): alto 34, padding 22×0, `CornerRadius` 17, fondo acento, texto blanco 14
  `Normal`. Hover: +8 % luminosidad; pulsado: escala 0.97 (`RenderTransform`, 80 ms); deshabilitado: opacidad 0.4.
- `PillOutlineButton`: igual pero fondo transparente, borde 1.2 px `#8CFFFFFF`, texto `Ink`; hover fondo `#14FFFFFF`.
- `PillGhostButton`: sin borde, fondo `#14FFFFFF` (para acciones secundarias).
- Que `AccentButton` y `GlassButton` actuales hereden visualmente de estos (redondeo píldora) para que
  todo el launcher cambie de golpe, sin tener que editar cada vista.
- **Botón JUGAR**: mantenlo como acción principal, pero en píldora grande (alto 48, radio 24, 18 px
  SemiBold) con degradado vertical del verde actual y un brillo interior de 1 px `#40FFFFFF` arriba.

### Color de acento
La referencia usa **azul `#3478F6`** (hover `#4A88FF`, pulsado `#2563D9`) para botones, insignias y
puntos, y **degradado violeta → rosa** (`#8B2CF5` → `#E11D74`) para elementos "propios" (como las
burbujas del chat). Decisión por defecto:
- `Accent` pasa a azul `#3478F6` para la UI general (botones píldora, insignias, pestaña con novedades).
- El **verde del launcher** se queda solo en JUGAR, la barra de progreso y el estado "lista/actualizada".
- Define ambos como tokens (`Accent`, `AccentHover`, `AccentPress`, `Brand`, `BrandHover`) para poder
  revertir cambiando dos líneas. Deja un comentario explicándolo.

### Paneles laterales y overlays (patrón de la 2.ª captura)
- Panel derecho opcional (detalles de la instancia o del proyecto): ancho 320, separado por `Hairline`,
  cabecera con avatar 96 px centrado y anillo, nombre 18 y subtítulo `Ink3`; debajo, secciones en
  **acordeón** (`Expander` con estilo `GlassExpander`: cabecera 52 px, texto 16, chevron `&#xE70D;`
  que rota 180° en 150 ms, sin bordes, separador `Hairline`).
- Filas de archivo (mods, capturas, logs): icono de tipo 28 px, nombre 15, botón de descarga/abrir a la derecha.
- Overlays modales (`ConfirmOverlay`, `InstallDialogOverlay`, `OptionsOverlay`, etc.): mismo cristal,
  radio 16, scrim `#8C04060A`, entrada con fade + escala 0.96 → 1 en 160 ms (`CubicEase EaseOut`).

### Campos y controles
- `GlassTextBox`: sin borde visible en reposo, fondo `#1F000000`, radio 8, alto 36; foco: borde 1.5 px
  acento + `#0AFFFFFF` de fondo. Mismo tratamiento para `ComboBox` (desplegable de cristal, radio 10, sombra).
- `CheckBox` / interruptor: interruptor estilo iOS, 38×22, pista acento cuando está activo.
- `ToolTip` y `ContextMenu`: cristal denso `#E61A1E2A`, radio 8, borde `Hairline`, padding 6.

## Restricciones técnicas

- **Rendimiento**: el desenfoque de `GlassPanel` es caro. Usa **un solo `GlassPanel` grande** para
  la ventana entera y dibuja barra lateral, cabecera y tarjetas como `Border` semitransparentes encima,
  en lugar de anidar paneles desenfocados. Solo los overlays y los popups tienen su propio `GlassPanel`.
  Activa `RenderOptions.BitmapScalingMode="LowQuality"` en el pincel del fondo si mejora el FPS,
  y comprueba que redimensionar la ventana va fluido.
- Esquinas redondeadas de la ventana: hazlo con el margen + `Border` con `CornerRadius` y el área
  exterior transparente usando `WindowChrome` con `GlassFrameThickness="-1"` **solo si** el fondo
  sigue viéndose bien; si no, deja la ventana rectangular rellena con el fondo aurora (como ahora) y
  aplica el radio solo al panel de cristal. En Windows 11 llama además a
  `DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2)` en `NativeMethods.cs`
  (con `LibraryImport`, como el resto del archivo) para que la propia ventana tenga esquinas redondeadas.
- Al maximizar compensa el borde invisible de `WindowChrome` (ya hay `OnStateChanged`; revísalo).
- Todos los colores y medidas nuevos van como recursos en `Glass.xaml` (nada de hex sueltos en las
  vistas, salvo los colores decorativos del banner). Estilos reutilizables en `Glass.xaml`; estilos
  de una sola vista en sus `Resources`.
- Usa `StaticResource` salvo que el recurso cambie en tiempo de ejecución.
- Animaciones: solo `Storyboard` en `VisualStateManager`/triggers, duraciones 80–200 ms, y ninguna
  si `SystemParameters.ClientAreaAnimation` es `false`.

## Accesibilidad
- Contraste mínimo 4.5:1 del texto principal sobre el cristal en el **peor punto del fondo** (la zona
  más clara de la imagen + aurora). Si no llega, sube la opacidad del tinte, no bajes el color del texto.
- Foco de teclado visible en todo: anillo 2 px acento con radio igual al del control
  (`FocusVisualStyle` común), nunca `{x:Null}` sin alternativa.
- Los semáforos y botones de solo icono conservan `AutomationProperties.Name` y `ToolTip`.
- Tab recorre: cabecera → barra lateral → contenido, en orden visual.

## Plan de trabajo (una fase por respuesta; compila al final de cada una)

1. **Tokens y estilos base** en `Glass.xaml`: tintes, `Hairline`, acentos, `PillButton`,
   `PillOutlineButton`, `PillGhostButton`, `TopTab`, `SidebarItem`, `GroupedList`/`GroupedRow`,
   `Card`, `IconTile`, `StatusDot`, `Badge`, `GlassExpander`, `TrafficLightButton`, foco común.
   Haz que los estilos antiguos (`GlassButton`, `AccentButton`…) adopten el nuevo look.
2. **Ventana y fondo**: aurora, `GlassPanel` único, radio, sombra, semáforos, cabecera con pestañas,
   búsqueda, campana y avatar; esquinas DWM en Windows 11.
3. **Barra lateral**: grupos, elementos, lista de instancias estilo chat con punto de estado,
   tarjeta del jugador y Opciones.
4. **Vista de instancia**: banner destacado, lista agrupada (versión, loader, mods, estado) y
   botón JUGAR píldora; panel derecho con acordeones si cabe.
5. **Explorar** (`BrowseView.xaml`): subpestañas unificadas, búsqueda, tarjetas en rejilla/lista con
   pie separado y botones píldora; filtros como grupos de la barra lateral.
6. **Overlays y popups**: cristal, radio, animación de entrada, `ComboBox`, `ToolTip`, `ContextMenu`,
   interruptores.
7. **Repaso**: quitar estilos/recursos que hayan quedado sin uso, contraste, teclado, ventana
   maximizada, escalado 100/125/150/200 %, ancho mínimo 1024×768.

## Verificación en cada fase
- `dotnet build Launcher -c Release` sin errores **ni avisos nuevos** de XAML.
- Arranca la app y comprueba: arrastrar, minimizar, maximizar/restaurar, cerrar; seleccionar
  instancia; JUGAR; abrir Explorar, buscar, instalar; abrir cada overlay; navegar solo con teclado.
- Revisa la ventana de salida de VS Code por errores de `Binding` (`System.Windows.Data Error`).
- Al terminar cada fase, resume en 3–5 viñetas qué archivos cambiaste y qué falta.

## Qué NO hacer
- No copiar logotipos, nombres ni iconos de Adobe, Messenger ni Apple; solo el **estilo**
  (cristal, radios, píldoras, semáforos genéricos, listas agrupadas).
- No reescribir vistas enteras desde cero si basta con cambiar estilos: prioriza el cambio en
  `Glass.xaml` y toca las vistas solo para la estructura nueva.
- No cambiar nombres de propiedades, comandos o `x:Name` existentes.
- No introducir `AllowsTransparency="True"` en la ventana principal (incompatible con WebView2 y lento).
