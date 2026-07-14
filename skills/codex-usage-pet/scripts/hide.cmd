@echo off
setlocal
set "APP=%~dp0..\..\..\bin\CodexUsagePet.exe"
if not exist "%APP%" (
  echo Codex Usage Pet executable was not found: "%APP%" 1>&2
  exit /b 2
)
"%APP%" --hide
exit /b %errorlevel%
