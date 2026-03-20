@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Abrir-Super.ps1"
if errorlevel 1 (
    echo.
    echo Falha ao abrir o super.
) else (
    echo.
    echo Super compilado e aberto com sucesso.
)
pause
