; Installer for Superior IPTV Player (NSIS 3). Built on the build server with:  makensis installer/setup.nsi
; Installs for the current user only, so it needs no administrator password.
Unicode true
!define APPNAME "Superior IPTV Player"
!define APPEXE "SuperiorIPTVPlayer.exe"
!define REGKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\SuperiorIPTVPlayer"
!ifndef APPVERSION
  !define APPVERSION "1.0.0"
!endif

Name "${APPNAME}"
OutFile "../dist/SuperiorIPTVPlayer-Setup.exe"
InstallDir "$LOCALAPPDATA\Programs\${APPNAME}"
InstallDirRegKey HKCU "${REGKEY}" "InstallLocation"
RequestExecutionLevel user
SetCompressor /SOLID lzma
BrandingText "${APPNAME} ${APPVERSION}"

!include "MUI2.nsh"
!include "x64.nsh"

!define MUI_ICON "../src/App/app.ico"
!define MUI_UNICON "../src/App/app.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APPEXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Start ${APPNAME}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

VIProductVersion "1.0.0.0"
VIAddVersionKey "ProductName" "${APPNAME}"
VIAddVersionKey "FileDescription" "${APPNAME} Setup"
VIAddVersionKey "FileVersion" "${APPVERSION}"
VIAddVersionKey "ProductVersion" "${APPVERSION}"
VIAddVersionKey "LegalCopyright" ""

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "${APPNAME} needs a 64-bit version of Windows 10 or Windows 11."
    Abort
  ${EndIf}
FunctionEnd

Section "Install"
  SetOutPath "$INSTDIR"
  File /r "../publish/*.*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateShortcut "$SMPROGRAMS\${APPNAME}.lnk" "$INSTDIR\${APPEXE}" "" "$INSTDIR\app.ico"
  CreateShortcut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\${APPEXE}" "" "$INSTDIR\app.ico"

  WriteRegStr HKCU "${REGKEY}" "DisplayName" "${APPNAME}"
  WriteRegStr HKCU "${REGKEY}" "DisplayVersion" "${APPVERSION}"
  WriteRegStr HKCU "${REGKEY}" "Publisher" "${APPNAME}"
  WriteRegStr HKCU "${REGKEY}" "DisplayIcon" "$INSTDIR\app.ico"
  WriteRegStr HKCU "${REGKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${REGKEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "${REGKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${REGKEY}" "NoRepair" 1
SectionEnd

Section "Uninstall"
  Delete "$SMPROGRAMS\${APPNAME}.lnk"
  Delete "$DESKTOP\${APPNAME}.lnk"
  ; Remove only what the installer put there. Settings, recordings and downloads are kept.
  RMDir /r "$INSTDIR\libvlc"
  Delete "$INSTDIR\*.dll"
  Delete "$INSTDIR\*.exe"
  Delete "$INSTDIR\*.json"
  Delete "$INSTDIR\*.pdb"
  Delete "$INSTDIR\*.ico"
  RMDir "$INSTDIR"
  DeleteRegKey HKCU "${REGKEY}"
SectionEnd
