@echo off
rem Servidor de produccion: todas las interfaces, puerto 59381.
rem Al arrancar revisa la carpeta del cliente y actualiza el manifest solo; despues vigila los cambios.
rem Carpeta del cliente: tests\Client por defecto (variable KONNOR_CLIENT_DIR para cambiarla).
chcp 65001 >nul
set PYTHONUTF8=1
title Konnor Launcher Backend
call "%~dp0preparar_entorno.bat" || (pause & exit /b 1)

"%PYTHON%" -m uvicorn main:app --host 0.0.0.0 --port 59381
pause
