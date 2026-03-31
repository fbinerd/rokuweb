@echo off
setlocal

REM Chama o launcher PowerShell com update/backup integrado
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Abrir-App.ps1"
set "EXIT_CODE=%ERRORLEVEL%"

if not "%EXIT_CODE%"=="0" (
    echo.
    echo Falha ao compilar, atualizar ou abrir o aplicativo.
    pause
)

exit /b %EXIT_CODE%
