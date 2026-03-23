Option Explicit

Dim fso
Dim shell
Dim repoRoot
Dim watchdogScript
Dim superExe
Dim command

Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

repoRoot = fso.GetParentFolderName(WScript.ScriptFullName)
watchdogScript = repoRoot & "\Run-SuperPainelWatchdog.ps1"
superExe = repoRoot & "\super\src\WindowManager.App\bin\Release\net481\SuperPainel.exe"

command = "powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File """ & watchdogScript & """ -ExePath """ & superExe & """"
shell.Run command, 0, False
