@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Gerar-Pacote-Roku.ps1"
if errorlevel 1 (
    echo.
    echo Falha ao gerar o pacote Roku.
) else (
    echo.
    echo Pacote Roku gerado com sucesso.
)
pause
