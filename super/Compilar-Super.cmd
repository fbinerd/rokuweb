@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Compilar-Super.ps1"
if errorlevel 1 (
    echo.
    echo Falha ao compilar o super.
) else (
    echo.
    echo Super compilado com sucesso.
)
pause
