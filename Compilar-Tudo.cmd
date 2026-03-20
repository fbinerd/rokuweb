@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Compilar-Tudo.ps1"
if errorlevel 1 (
    echo.
    echo Falha ao compilar os projetos.
) else (
    echo.
    echo Compilacao conjunta concluida.
)
pause
