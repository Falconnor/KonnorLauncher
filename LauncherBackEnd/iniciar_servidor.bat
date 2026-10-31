@echo off
rem Servidor de produccion sin panel. Escucha donde diga backend_config.json (por defecto 0.0.0.0:59381).
rem Al arrancar revisa la carpeta del cliente y actualiza el manifest solo; despues vigila los cambios.
rem Las variables KONNOR_* (por ejemplo KONNOR_CLIENT_DIR) tienen prioridad sobre backend_config.json.
chcp 65001 >nul
set PYTHONUTF8=1
title Konnor Launcher Backend
call "%~dp0preparar_entorno.bat" || (pause & exit /b 1)

"%PYTHON%" -m Services.serve
pause
