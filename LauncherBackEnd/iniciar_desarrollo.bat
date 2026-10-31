@echo off
rem Servidor de desarrollo: solo local (127.0.0.1:8000) y con recarga automatica al editar el codigo.
rem La recarga vigila solo los .py; la carpeta del cliente la vigila el propio servidor.
chcp 65001 >nul
set PYTHONUTF8=1
title Servidor de Konnor Launcher (desarrollo)
call "%~dp0preparar_entorno.bat" || (pause & exit /b 1)

"%PYTHON%" -m uvicorn main:app --host 127.0.0.1 --port 8000 --reload --reload-dir Services --reload-include "*.py"
pause
