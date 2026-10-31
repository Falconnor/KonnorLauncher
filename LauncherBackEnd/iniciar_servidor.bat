@echo off
rem Servidor de produccion sin panel.
rem   iniciar_servidor.bat          servidor seleccionado en el panel
rem   iniciar_servidor.bat <id>     otro servidor de servidores.json (por ejemplo: principal)
rem Al arrancar revisa la carpeta del cliente y actualiza el manifest solo; despues vigila los cambios.
rem Las variables KONNOR_* (por ejemplo KONNOR_CLIENT_DIR) tienen prioridad sobre servidores.json.
chcp 65001 >nul
set PYTHONUTF8=1
title Servidor de Konnor Launcher %~1
call "%~dp0preparar_entorno.bat" || (pause & exit /b 1)

if "%~1"=="" (
    "%PYTHON%" -m Services.serve
) else (
    "%PYTHON%" -m Services.serve --servidor "%~1"
)
pause
