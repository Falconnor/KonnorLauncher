# Fkonnor Launcher - Contexto de Sesión

## Estado del Proyecto
*   **Tecnologías:** Python 3, PySide6 (Qt) para la interfaz, FastAPI para el servidor de actualización.
*   **Objetivo:** Reemplazar un antiguo launcher basado en PowerShell con una solución robusta y limpia en Python, manteniendo un diseño visual "glass" (cristalizado) y minimalista.

## Avances Recientes
1.  **Diseño Visual (UI):**
    *   **Estilo Glass:** Se implementó una interfaz sin marcos (`FramelessWindowHint`), con fondo translúcido, eliminando cajas grises oscuras. Los controles usan blancos semitransparentes (`rgba(255, 255, 255, x)`) con texto gris carbón o blanco.
    *   **Botón JUGAR:** Se rediseñó con un gradiente "escalonado" (stepped) para emular un estilo pixel art 3D (luz fina arriba, color plano al medio, sombra fina abajo y un borde oscuro de 3px).
    *   **Tipografía:** Se reemplazó "Arial" por la fuente personalizada **`Boring Time.otf`** en toda la aplicación. Se solucionaron problemas de "bordes de sierra" en el texto aplicando estrategias de antialiasing (`PreferOutline`, `PreferAntialias`) y arreglando variables de entorno para alta resolución.
2.  **Panel de Opciones:**
    *   Se agregó un botón de opciones que abre un `SettingsDialog` moderno (QDialog translúcido).
    *   Gestiona Memoria RAM (`config.json`), Usuario (`config.json`), Ruta de Java (`launcher.properties`), URL del servidor y comprobación de verificación automática.
3.  **Optimización de Arranque:**
    *   Se eliminó el bloqueo (congelamiento) de la UI que ocurría al iniciar mientras se consultaba la versión al servidor. 
    *   Ahora lee inmediatamente una versión de caché (`cached.client.version` en `launcher.properties`) y realiza la consulta en segundo plano mediante `VersionFetchRunnable`. El arranque es instantáneo.
4.  **Servidor de Actualización:**
    *   Actualmente corre en **FastAPI**. Se discutieron alternativas (Nginx, Caddy, Go, Node.js).
    *   Se creó un script rápido `actualizar_manifest.bat` en la carpeta del servidor para generar hashes y actualizar el manifest en un clic sin reiniciar el servidor.

## Archivos Clave
*   **`launcher_core.py`:** Lógica central, manejo de directorios, lectura/escritura de propiedades y ejecución del juego.
*   **`ui.py`:** Interfaz gráfica completa en PySide6, gestión de hilos (runnables), menús y diálogos.
*   **`verify_client.py`:** Lógica de descarga en paralelo de bloques `.zip` de assets y archivos individuales.
*   **`launcher.properties`:** Preferencias técnicas del launcher (Ruta Java, servidor, caché, versión, flags).
*   **`config.json`:** Configuración de juego en estándar JSON (nombre de usuario, RAM).

## Siguientes Pasos Potenciales
*   Decisión sobre la tecnología backend del servidor (Nginx recomendado si solo sirve archivos estáticos).
*   Pruebas completas del flujo de verificación y ejecución (inicio de juego).

