# Contexto de la Sesión - Fkonnor Launcher

Este archivo documenta el estado actual del proyecto, los avances recientes y los detalles técnicos más importantes de la sesión, sirviendo como punto de referencia para futuras continuaciones.

## 1. Cambios Arquitectónicos Recientes
* **Eliminación de `config.json`**: Se eliminó por completo la dependencia del archivo `config.json` local. Todas las configuraciones (incluyendo `player.username` y `java.memory`) ahora se leen y escriben estrictamente en `launcher.properties`. Esto independiza el launcher de los archivos internos del juego y centraliza la configuración.
* **Recarga en Tiempo Real**: Se modificó `launcher_core.save_property()` para que actualice la caché global (`PROPERTIES` y `GAME_PATH`) en el momento en que se escribe en el disco. Gracias a esto, cualquier cambio (como elegir una nueva carpeta de Minecraft) se refleja instantáneamente en el menú de versiones de la UI principal sin necesidad de reiniciar el programa.

## 2. Mejoras en la Interfaz (UI)
* **Rediseño del Panel de Opciones (`ui.py`)**: 
  * Se implementó un diseño moderno "Glassmorphism" con un panel oscuro (560x380) centrado.
  * Se utilizó `QGridLayout` para ordenar los elementos y se agregó un `QScrollArea` con una barra de desplazamiento estilizada, evitando modificar el alto fijo de la ventana.
  * La fuente base se globalizó a "Boring Time", añadiendo soporte de antialiasing y HighDPI en `launcher.py` para evitar bordes pixelados en fondos transparentes.
* **Manejo de RAM para la JVM**:
  * El menú desplegable de memoria RAM ahora muestra visualmente "GB" (ej. "4 GB") para el usuario, pero internamente utiliza el atributo `itemData` para guardar el valor exacto que necesita Java ("4G").
* **Arrastre del Launcher con Opciones Abiertas**:
  * Se ajustó el diálogo de opciones (`SettingsDialog`) para que mida `1275x700` (el tamaño total del launcher) con un fondo del `1%` de opacidad (`rgba(0,0,0,0.01)`). Esto soluciona un problema nativo de Windows con la transparencia total y permite al usuario **arrastrar toda la ventana del launcher** haciendo clic en cualquier parte, manteniendo intacto el comportamiento modal de seguridad (no se pueden cliquear los botones del menú trasero mientras configuras).

## 3. Correcciones de Errores (Bugs Fixes)
* **`assetsDir` no definido**: Se corrigió el argumento de lanzamiento de Java que apuntaba a una variable no definida. Ahora vuelve a construir la ruta dinámicamente como `os.path.join(game, "assets")`.
* **Crash al cerrar Opciones**: Se eliminó una referencia "fantasma" a la antigua función `load_config()` que hacía que el programa colapsara al darle al botón "GUARDAR".
* **Selección de Directorio**: Se arregló el hecho de que el botón de jugar y el menú de versiones apuntaran a la carpeta vieja; la actualización inmediata de la variable `GAME_PATH` solucionó esto.

## 4. Estado Técnico / Pendientes
* El backend de actualización y el sistema de `LauncherLiveServer` ya cuenta con el script `.bat` para regenerar manifiestos y la lógica de inicio instantáneo usando una lectura asíncrona de la versión (`cached.client.version`).
* El flujo actual es estable: inicia rápido, respeta los archivos de configuración en `.properties`, permite jugar en una versión específica instalada y la interfaz mantiene un diseño pulido sin bugs visuales aparentes.

