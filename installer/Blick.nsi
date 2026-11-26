Unicode true
!include "MUI2.nsh"
Name "Blick Preview"
OutFile "..\Blick-Setup.exe"
InstallDir "$LOCALAPPDATA\Programs\Blick Preview"
RequestExecutionLevel user
Icon "..\blick.ico"
UninstallIcon "..\blick.ico"
!define MUI_ICON "..\blick.ico"
!define MUI_UNICON "..\blick.ico"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Russian"
!insertmacro MUI_LANGUAGE "English"

Section "Blick"
  SetOutPath "$INSTDIR"
  File "..\Blick.exe"
  File /oname=Inter-LICENSE.txt "..\assets\fonts\LICENSE.txt"
  File /oname=Lucide-LICENSE.txt "..\assets\lucide\LICENSE"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateShortcut "$SMPROGRAMS\Blick Preview.lnk" "$INSTDIR\Blick.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\BlickPreview" "DisplayName" "Blick Preview"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\BlickPreview" "DisplayIcon" "$INSTDIR\Blick.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\BlickPreview" "UninstallString" '"$INSTDIR\Uninstall.exe"'
SectionEnd

Section "Uninstall"
  Delete "$SMPROGRAMS\Blick Preview.lnk"
  Delete "$INSTDIR\Blick.exe"
  Delete "$INSTDIR\Inter-LICENSE.txt"
  Delete "$INSTDIR\Lucide-LICENSE.txt"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\BlickPreview"
SectionEnd
