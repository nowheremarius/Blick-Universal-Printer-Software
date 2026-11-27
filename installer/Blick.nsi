Unicode true
!include "MUI2.nsh"
!ifdef STABLE
!define PRODUCT "Blick"
!define PRODUCT_KEY "Blick"
!else
!define PRODUCT "Blick Preview"
!define PRODUCT_KEY "BlickPreview"
!endif
Name "${PRODUCT}"
OutFile "..\Blick-Setup.exe"
InstallDir "$LOCALAPPDATA\Programs\${PRODUCT}"
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
  CreateShortcut "$SMPROGRAMS\${PRODUCT}.lnk" "$INSTDIR\Blick.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_KEY}" "DisplayName" "${PRODUCT}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_KEY}" "DisplayIcon" "$INSTDIR\Blick.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
SectionEnd

Section "Uninstall"
  Delete "$SMPROGRAMS\${PRODUCT}.lnk"
  Delete "$INSTDIR\Blick.exe"
  Delete "$INSTDIR\Inter-LICENSE.txt"
  Delete "$INSTDIR\Lucide-LICENSE.txt"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_KEY}"
SectionEnd
