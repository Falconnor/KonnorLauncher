@echo off
rem Regenera el manifest a mano (normalmente no hace falta: el servidor lo hace solo).
rem   actualizar_manifest.bat                    solo rehace lo que cambio
rem   actualizar_manifest.bat --forzar-bloques   rehace tambien todos los ZIP de assets
rem   actualizar_manifest.bat --servidor <id>     otro servidor (por defecto, el seleccionado en el panel)
chcp 65001 >nul
set PYTHONUTF8=1
title Actualizar manifest - Servidor de Konnor Launcher
call "%~dp0preparar_entorno.bat" || (pause & exit /b 1)

"%PYTHON%" -m Services.generate_manifest %*
if errorlevel 1 (
    echo.
    echo [ERROR] No se pudo generar el manifest. El anterior sigue publicado sin cambios.
    pause
    exit /b 1
)
pause
