@echo off
rem Panel de control: arranca y detiene el servidor, edita la configuracion y muestra su estado.
rem Se abre en una ventana propia (Edge o Chrome en modo aplicacion) en http://127.0.0.1:59380
rem Cerrar esta consola cierra el panel y el servidor que haya arrancado.
chcp 65001 >nul
set PYTHONUTF8=1
title Panel - Servidor de Konnor Launcher
call "%~dp0preparar_entorno.bat" || (pause & exit /b 1)

"%PYTHON%" -m Panel %*
if errorlevel 1 pause
