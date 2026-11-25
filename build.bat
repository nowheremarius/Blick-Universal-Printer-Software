@echo off
chcp 65001 >nul
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Could not find the .NET Framework 4.x compiler.
  pause
  exit /b 1
)
echo Building Blick...
"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /out:Blick.exe /win32icon:blick.ico /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Microsoft.VisualBasic.dll Theme.cs Controls.cs Preview.cs Imaging.cs Scan.cs MainForm.cs
if errorlevel 1 (
  echo.
  echo Build failed. See the compiler output above.
  pause
  exit /b 1
)
echo.
echo Built Blick.exe in the project folder.
pause
