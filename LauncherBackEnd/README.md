# Konnor Launcher Backend

Servidor de actualización de Konnor Launcher. Cuando el launcher tiene activada la casilla
**VERIFICAR ARCHIVOS AL INICIAR**, descarga de aquí el manifest del cliente, lo compara con los
archivos del jugador y descarga lo que falte o haya cambiado. Así todos los jugadores tienen
exactamente los mismos mods, versiones, librerías y assets.

Los miles de archivos pequeños de `assets/` se empaquetan en 16 bloques ZIP: el launcher descarga
un bloque mientras extrae el anterior, en lugar de hacer una petición por cada archivo.

## Estructura

```
main.py                        ← servidor FastAPI (uvicorn)
Panel/                         ← panel de control (interfaz web local, 127.0.0.1:59380)
  app.py                       ← API del panel
  supervisor.py                ← arranque y parada del servidor, registro y actividad
  static/                      ← interfaz (HTML, CSS, JS)
Services/
  settings.py                  ← configuración (backend_config.json y variables de entorno)
  serve.py                     ← arranca uvicorn con el host y el puerto configurados
  generate_manifest.py         ← genera client_manifest.json
  generate_asset_blocks.py     ← bloques ZIP de assets (incrementales)
  auto_update.py               ← actualización automática al arrancar y al cambiar el cliente
  hash_cache.py                ← caché de hashes (tamaño + fecha de modificación)
  client_version.txt           ← versión del cliente que anuncia el manifest
tests/Client/                  ← archivos de prueba del cliente (no está en git)
  mods/  versions/  libraries/  assets/
  packages/                    ← bloques generados
iniciar_panel.bat              ← panel de control (recomendado)
iniciar_servidor.bat           ← producción sin panel: escucha donde diga la configuración
iniciar_desarrollo.bat         ← desarrollo: 127.0.0.1:8000, recarga el código al editarlo
actualizar_manifest.bat        ← regeneración manual (normalmente no hace falta)
preparar_entorno.bat           ← crea .venv e instala requirements.txt (lo usan los demás)
```

## Puesta en marcha

```powershell
iniciar_panel.bat
```

La primera vez crea el entorno `.venv` e instala las dependencias. Después abre el panel de
control en una ventana propia (Edge o Chrome en modo aplicación; si no hay ninguno, el navegador
predeterminado). Desde el panel:

- **Servidor**: iniciar, detener y reiniciar; estado, PID, tiempo activo, la línea `server.url=`
  lista para copiar en el `launcher.properties` de los jugadores y la actividad desde el arranque
  (verificaciones, archivos servidos, equipos distintos y respuestas con error).
- **Manifest**: versión publicada (editable), archivos, bloques y assets, estado de la
  actualización automática y regeneración manual. Con el servidor detenido, la regeneración se
  hace desde el propio panel.
- **Configuración**: red, carpeta del cliente, actualización automática y rutas. Se guarda en
  `backend_config.json`; si el servidor está en marcha, el panel avisa de que hay que reiniciarlo.
- **Registro**: salida del servidor en vivo, con filtro de avisos y errores.

El panel solo escucha en `127.0.0.1`. Cerrar su consola detiene también el servidor que arrancó.
Sin panel, `iniciar_servidor.bat` arranca el servidor con la misma configuración. Al arrancar,
el servidor:

1. Revisa la carpeta del cliente y actualiza el manifest en segundo plano. El servidor responde
   desde el primer momento: mientras tanto se sirve el manifest anterior.
2. Vigila la carpeta y vuelve a actualizar el manifest cada vez que algo cambia, cuando la
   carpeta lleva unos segundos sin modificarse (para no hacerlo a mitad de una copia).

No hace falta regenerar el manifest a mano ni reiniciar el servidor.

## Publicar cambios en el cliente

1. Copiar los cambios a la carpeta del cliente (por ejemplo, un mod nuevo en `mods/`).
2. Si cambia el modpack, subir la versión en `Services/client_version.txt`.

El servidor detecta los cambios y publica el nuevo manifest. Solo se recalculan los hashes de
los archivos modificados y solo se rehacen los bloques de assets que cambiaron.

Si alguna ruta no es válida para el launcher (caracteres como `:` o `?`, nombres que terminan
en punto...), el manifest publicado no se modifica y el error aparece en la consola.

## Endpoints

El launcher depende de estas rutas exactas:

| Ruta | Contenido |
|---|---|
| `GET /` | Estado del servidor, versión publicada y estado de la actualización automática |
| `GET /client_manifest.json` | Manifest (sin caché; 503 mientras se genera por primera vez, 404 si no existe) |
| `GET /client/{ruta}` | Archivos de la carpeta del cliente |
| `GET /client/packages/{bloque}` | Bloques ZIP de assets |
| `POST /admin/regenerate` | Regeneración bajo demanda; solo desde el propio equipo (la usa el panel) |

Las descargas admiten `Range` (reanudación), `ETag` y `Last-Modified`.

## Configuración

Se edita desde el panel y se guarda en `backend_config.json` (no está en git). Cada valor se puede
forzar con una variable de entorno, que tiene prioridad al usar los `.bat`; el servidor que arranca
el panel usa siempre los valores del panel.

| Variable | Por defecto | Uso |
|---|---|---|
| `KONNOR_HOST` | `0.0.0.0` | Interfaz en la que escucha el servidor |
| `KONNOR_PORT` | `59381` | Puerto del servidor |
| `KONNOR_CLIENT_DIR` | `tests/Client` | Carpeta con los archivos del cliente |
| `KONNOR_MANIFEST` | `client_manifest.json` | Manifest generado |
| `KONNOR_CLIENT_VERSION_FILE` | `Services/client_version.txt` | Versión anunciada |
| `KONNOR_CACHE_DIR` | `.cache` | Caché de hashes |
| `KONNOR_AUTO_UPDATE` | `1` | Actualizar el manifest al arrancar |
| `KONNOR_WATCH` | `1` | Vigilar la carpeta del cliente |
| `KONNOR_WATCH_QUIET_MS` | `3000` | Milisegundos sin cambios antes de regenerar |
| `KONNOR_PANEL_PORT` | `59380` | Puerto del panel de control (solo 127.0.0.1) |

Ejemplo para producción con otra carpeta: `set KONNOR_CLIENT_DIR=D:\KonnorClient` antes de
`iniciar_servidor.bat`.

## Notas

- Con `delete_unauthorized=true` en el launcher, todo lo que haya en `mods/`, `versions/`,
  `libraries/` y `assets/` del jugador y no esté en el manifest se borra. Por eso la carpeta del
  cliente debe contener el cliente completo, incluidas las librerías y la versión base del juego.
- Los bloques llevan su hash en el nombre (`assets_block_3-1a2b3c4d5e6f.zip`) y no se sobrescriben
  nunca. Se conservan los del manifest actual y los del anterior, para que quien esté descargando
  durante una actualización no reciba un archivo distinto del esperado.
- `packages/` solo debe contener bloques generados: cualquier otro archivo que haya ahí también
  se puede descargar desde `/client/packages/` (el servidor lo avisa en la consola).
