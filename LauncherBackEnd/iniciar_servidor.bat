@echo off
rem Servidor de produccion sin panel.
rem   iniciar_servidor.bat          backend seleccionado en el panel
rem   iniciar_servidor.bat <id>     otro backend de backends.json (por ejemplo: principal)
rem Al arrancar revisa la carpeta del cliente y actualiza el manifest solo; despues vigila los cambios.
rem Las variables KONNOR_* (por ejemplo KONNOR_CLIENT_DIR) tienen prioridad sobre backends.json.
chcp 65001 >nul
set PYTHONUTF8=1
title Konnor Launcher Backend %~1
call "%~dp0preparar_entorno.bat" || (pause & exit /b 1)

if "%~1"=="" (
    "%PYTHON%" -m Services.serve
) else (
    "%PYTHON%" -m Services.serve --backend "%~1"
)
pause
