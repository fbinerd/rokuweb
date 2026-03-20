@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Compilar-Projetos.ps1"
if errorlevel 1 (
    echo.
    echo Falha ao compilar os projetos.
) else (
    echo.
    echo Rokuweb e super compilados com sucesso.
)
pause
