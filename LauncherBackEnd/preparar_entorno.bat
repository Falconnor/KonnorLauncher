@echo off
rem Prepara el entorno de Python del backend (lo llaman los demas .bat).
rem Crea .venv la primera vez e instala requirements.txt; solo reinstala si ese archivo cambia.
rem Al terminar deja en PYTHON la ruta del interprete del entorno.
cd /d "%~dp0"
set "PYTHON=%~dp0.venv\Scripts\python.exe"

if not exist "%PYTHON%" (
    echo Creando entorno virtual .venv ...
    python -m venv .venv
    if errorlevel 1 (
        echo [ERROR] No se pudo crear .venv. Comprueba que Python 3.12 o superior este instalado y en el PATH.
        exit /b 1
    )
)

fc /b requirements.txt ".venv\requirements.instalado" >nul 2>&1
if errorlevel 1 (
    echo Instalando dependencias ...
    "%PYTHON%" -m pip install --disable-pip-version-check -q -r requirements.txt
    if errorlevel 1 (
        echo [ERROR] No se pudieron instalar las dependencias.
        exit /b 1
    )
    copy /y requirements.txt ".venv\requirements.instalado" >nul
)
exit /b 0
