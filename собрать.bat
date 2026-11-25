@echo off
chcp 65001 >nul
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Не найден встроенный компилятор .NET Framework 4.
  echo Сделайте скриншот этого окна и отправьте Claude.
  pause
  exit /b 1
)
echo Собираю Блик...
if exist Blik.cs del Blik.cs
"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /out:Blik.exe /win32icon:blik.ico /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Microsoft.VisualBasic.dll Theme.cs Controls.cs Preview.cs Imaging.cs Scan.cs MainForm.cs
if errorlevel 1 (
  echo.
  echo Сборка не удалась. Сделайте скриншот этого окна и отправьте Claude.
  pause
  exit /b 1
)
echo.
echo Готово! В этой папке обновился Blik.exe - запускайте его.
pause
