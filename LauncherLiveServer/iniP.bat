@echo off
cd /d "C:\Users\Luis\Desktop\LauncherLiveServer"

python -m uvicorn main:app --host 0.0.0.0 --port 59381

pause