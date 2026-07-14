@echo off
setlocal
set "ROOT=%~dp0.."
set "FX=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FX%\csc.exe" set "FX=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%FX%\csc.exe" (
  echo .NET Framework C# compiler was not found. 1>&2
  exit /b 2
)
if not exist "%ROOT%\bin" mkdir "%ROOT%\bin"

"%FX%\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /langversion:5 /codepage:65001 ^
  /out:"%ROOT%\bin\CodexUsagePet.exe" ^
  /reference:"%FX%\WPF\PresentationCore.dll" ^
  /reference:"%FX%\WPF\PresentationFramework.dll" ^
  /reference:"%FX%\WPF\WindowsBase.dll" ^
  /reference:"%FX%\System.Xaml.dll" ^
  /reference:"%FX%\System.Windows.Forms.dll" ^
  /reference:"%FX%\System.Drawing.dll" ^
  /reference:"%FX%\System.Web.Extensions.dll" ^
  "%ROOT%\src\CodexUsagePet.cs"

if errorlevel 1 exit /b %errorlevel%

"%FX%\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /langversion:5 /codepage:65001 ^
  /out:"%ROOT%\bin\CodexUsagePetWatcher.exe" ^
  "%ROOT%\src\CodexUsagePetWatcher.cs"

exit /b %errorlevel%
