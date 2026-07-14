@echo off
setlocal

set "RUN_KEY=HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
set "RUN_NAME=CodexUsagePetAutoStart"
set "LEGACY_RUN_NAME=CodexUsagePetWatcher"

if not defined LOCALAPPDATA (
  echo LOCALAPPDATA is not available; autostart was not changed. 1>&2
  exit /b 2
)

rem Resolve this installed plugin from the script itself. This works from a
rem Codex marketplace checkout, a versioned cache, or any copied plugin path.
for %%I in ("%~dp0..\..\..") do set "PLUGIN_ROOT=%%~fI"
set "SOURCE_PET=%PLUGIN_ROOT%\bin\CodexUsagePet.exe"
set "SOURCE_WATCHER=%PLUGIN_ROOT%\bin\CodexUsagePetWatcher.exe"
set "SOURCE_MASCOT=%PLUGIN_ROOT%\assets\mascot.png"

set "RUNTIME_ROOT=%LOCALAPPDATA%\CodexUsagePet\runtime"
set "RUNTIME_BIN=%RUNTIME_ROOT%\bin"
set "RUNTIME_ASSETS=%RUNTIME_ROOT%\assets"
set "RUNTIME_PET=%RUNTIME_BIN%\CodexUsagePet.exe"
set "RUNTIME_WATCHER=%RUNTIME_BIN%\CodexUsagePetWatcher.exe"
set "RUNTIME_MASCOT=%RUNTIME_ASSETS%\mascot.png"

if not exist "%SOURCE_PET%" goto missing_source
if not exist "%SOURCE_WATCHER%" goto missing_source
if not exist "%SOURCE_MASCOT%" goto missing_source

rem Any copy of the watcher can signal the shared named stop event. Prefer the
rem current runtime copy, then use the watcher bundled with this installation.
if exist "%RUNTIME_WATCHER%" (
  "%RUNTIME_WATCHER%" --stop >nul 2>&1
  if errorlevel 1 (
    "%SOURCE_WATCHER%" --stop >nul 2>&1
    if errorlevel 1 goto stop_failed
  )
) else (
  "%SOURCE_WATCHER%" --stop >nul 2>&1
  if errorlevel 1 goto stop_failed
)

rem Close a running runtime pet before replacing its executable. Its normal
rem quit path saves position.txt and the watcher will show it again for Codex.
if exist "%RUNTIME_PET%" (
  "%RUNTIME_PET%" --quit >nul 2>&1
  if errorlevel 1 "%SOURCE_PET%" --quit >nul 2>&1
) else (
  "%SOURCE_PET%" --quit >nul 2>&1
)
powershell.exe -NoProfile -NonInteractive -Command "Start-Sleep -Milliseconds 500" >nul 2>&1

if not exist "%RUNTIME_BIN%\" md "%RUNTIME_BIN%" >nul 2>&1
if not exist "%RUNTIME_BIN%\" goto runtime_directory_failed
if not exist "%RUNTIME_ASSETS%\" md "%RUNTIME_ASSETS%" >nul 2>&1
if not exist "%RUNTIME_ASSETS%\" goto runtime_directory_failed

call :copy_verified "%SOURCE_PET%" "%RUNTIME_PET%"
if errorlevel 1 goto copy_failed
call :copy_verified "%SOURCE_WATCHER%" "%RUNTIME_WATCHER%"
if errorlevel 1 goto copy_failed
call :copy_verified "%SOURCE_MASCOT%" "%RUNTIME_MASCOT%"
if errorlevel 1 goto copy_failed

rem Remove the legacy value used by older local builds before registering the
rem portable runtime path.
reg.exe query "%RUN_KEY%" /v "%LEGACY_RUN_NAME%" >nul 2>&1
if not errorlevel 1 (
  reg.exe delete "%RUN_KEY%" /v "%LEGACY_RUN_NAME%" /f >nul 2>&1
  if errorlevel 1 goto registry_failed
)

reg.exe add "%RUN_KEY%" /v "%RUN_NAME%" /t REG_SZ /d "\"%RUNTIME_WATCHER%\"" /f >nul 2>&1
if errorlevel 1 goto registry_failed

start "" "%RUNTIME_WATCHER%"
if errorlevel 1 goto start_failed

echo Codex Usage Pet autostart is enabled.
exit /b 0

:copy_verified
copy /y /b "%~1" "%~2" >nul 2>&1
if errorlevel 1 (
  powershell.exe -NoProfile -NonInteractive -Command "Start-Sleep -Milliseconds 500" >nul 2>&1
  copy /y /b "%~1" "%~2" >nul 2>&1
  if errorlevel 1 exit /b 1
)
if not exist "%~2" exit /b 1
fc.exe /b "%~1" "%~2" >nul 2>&1
if errorlevel 1 exit /b 1
exit /b 0

:missing_source
echo Codex Usage Pet runtime files were not found under "%PLUGIN_ROOT%". 1>&2
exit /b 2

:stop_failed
echo The existing Codex Usage Pet watcher could not be stopped. 1>&2
exit /b 3

:runtime_directory_failed
echo The runtime directory could not be created at "%RUNTIME_ROOT%". 1>&2
exit /b 4

:copy_failed
echo Codex Usage Pet runtime files could not be copied to "%RUNTIME_ROOT%". 1>&2
exit /b 5

:registry_failed
echo The Codex Usage Pet autostart registry value could not be written. 1>&2
exit /b 6

:start_failed
reg.exe delete "%RUN_KEY%" /v "%RUN_NAME%" /f >nul 2>&1
echo The Codex Usage Pet watcher could not be started. 1>&2
exit /b 7
