@echo off
title Actualizar Manifest del Servidor
cd /d "%~dp0"

echo.
echo  ╔══════════════════════════════════════════╗
echo  ║   ACTUALIZAR MANIFEST DEL SERVIDOR       ║
echo  ╚══════════════════════════════════════════╝
echo.

echo [1/2] Generando manifest...
echo.
cd Services
python generate_manifest.py
if %ERRORLEVEL% neq 0 (
    echo.
    echo [ERROR] Fallo al generar el manifest.
    pause
    exit /b 1
)

echo.
echo [2/2] Manifest actualizado correctamente.
echo.
echo  Archivo: %~dp0client_manifest.json
echo.
echo  El servidor servira el nuevo manifest automaticamente.
echo  No es necesario reiniciarlo.
echo.
pause

