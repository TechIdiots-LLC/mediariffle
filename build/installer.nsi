; Windows installer for MediaRiffle as a Windows service. Built by Build-Installer.ps1, which passes these defines:
;   APP_NAME, APP_EXE, VERSION, VERSION_NUMERIC, ARCH, PUBLISHER, FILE_LIST, OUTFILE, CONFIG_TEMPLATE, APP_ICON (optional)
;
; Installs the program to Program Files and runs it as the "MediaRiffle" service under its own virtual account,
; NT SERVICE\MediaRiffle. On the network that account is the computer itself (DOMAIN\COMPUTER$), so a file share
; can be opened to it without a password to keep (docs/file-server.md); the service's log-on can also be changed to a
; gMSA or domain account later. Settings and data live in %ProgramData%\MediaRiffle: the settings file is written only
; when there isn't one, and uninstalling leaves that folder alone.
;
; Like VistumblerMAUI's installer, the uninstaller removes only the files this installer put down (FILE_LIST), and an
; upgrade runs the old version's uninstaller first. Here the service is stopped before files are replaced and started
; again after.

Unicode true
ManifestDPIAware true

!include "MUI2.nsh"
!include "x64.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"

!macro RequireDefine NAME
  !ifndef ${NAME}
    !error "${NAME} is not defined: build this installer with Build-Installer.ps1"
  !endif
!macroend
!insertmacro RequireDefine APP_NAME
!insertmacro RequireDefine APP_EXE
!insertmacro RequireDefine VERSION
!insertmacro RequireDefine VERSION_NUMERIC
!insertmacro RequireDefine ARCH
!insertmacro RequireDefine PUBLISHER
!insertmacro RequireDefine FILE_LIST
!insertmacro RequireDefine CONFIG_TEMPLATE
!insertmacro RequireDefine OUTFILE

!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"
!define SERVICE "MediaRiffle"
!define SERVICE_ACCOUNT "NT SERVICE\MediaRiffle"
!define DEFAULT_URL "http://localhost:5080/"

Name "${APP_NAME} ${VERSION}"
OutFile "${OUTFILE}"
InstallDir "$PROGRAMFILES64\${APP_NAME}"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
BrandingText "${APP_NAME} ${VERSION} (${ARCH})"

VIProductVersion "${VERSION_NUMERIC}"
VIAddVersionKey "ProductName" "${APP_NAME}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "FileVersion" "${VERSION_NUMERIC}"
VIAddVersionKey "FileDescription" "${APP_NAME} installer (${ARCH})"
VIAddVersionKey "CompanyName" "${PUBLISHER}"
VIAddVersionKey "LegalCopyright" "${PUBLISHER}"

!ifdef APP_ICON
  !define MUI_ICON "${APP_ICON}"
  !define MUI_UNICON "${APP_ICON}"
!endif
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_TEXT "MediaRiffle is installed and running as a Windows service.$\r$\n$\r$\nSettings: %ProgramData%\MediaRiffle\appsettings.json (set the ffmpeg folder there, then restart the service).$\r$\n$\r$\nThe first visit creates the admin account."
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "Open MediaRiffle (${DEFAULT_URL})"
!define MUI_FINISHPAGE_RUN_FUNCTION OpenSite

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

!include "${FILE_LIST}"

Function .onInit
  SetRegView 64
  SetShellVarContext all
  !if "${ARCH}" == "arm64"
    ${IfNot} ${IsNativeARM64}
      MessageBox MB_ICONSTOP "This installer is for Windows on ARM64. Download the x64 installer for this server." /SD IDOK
      Abort
    ${EndIf}
  !else
    ${IfNot} ${IsNativeAMD64}
    ${AndIfNot} ${IsNativeARM64}
      MessageBox MB_ICONSTOP "${APP_NAME} needs 64-bit Windows." /SD IDOK
      Abort
    ${EndIf}
  !endif

  ; Upgrade in place (the 64-bit registry view holds the install location). An explicit /D= still wins.
  ${If} $INSTDIR == "$PROGRAMFILES64\${APP_NAME}"
    ReadRegStr $0 HKLM "${UNINST_KEY}" "InstallLocation"
    ${If} $0 != ""
      StrCpy $INSTDIR $0
    ${EndIf}
  ${EndIf}
FunctionEnd

Function un.onInit
  SetRegView 64
  SetShellVarContext all
FunctionEnd

Function OpenSite
  ExecShell "open" "${DEFAULT_URL}"
FunctionEnd

; net stop waits for the service to finish stopping (and its ffmpeg processes with it)
!macro StopService
  nsExec::ExecToLog 'net stop "${SERVICE}"'
  Pop $0
!macroend

Section "${APP_NAME} service (required)" SecApp
  SectionIn RO

  !insertmacro StopService

  ; Upgrade: let the installed version remove its own files first. /UPGRADE keeps the service and registration.
  IfFileExists "$INSTDIR\uninstall.exe" 0 +3
    ExecWait '"$INSTDIR\uninstall.exe" /S /UPGRADE _?=$INSTDIR'
    Delete "$INSTDIR\uninstall.exe"

  !insertmacro InstallFiles

  SetOutPath "$INSTDIR"
  WriteUninstaller "$INSTDIR\uninstall.exe"

  ; The service: created on first install, its program path brought up to date on upgrades. Delayed start, and
  ; restarted by Windows if it stops unexpectedly.
  nsExec::ExecToLog 'sc.exe query "${SERVICE}"'
  Pop $0
  ${If} $0 != 0
    nsExec::ExecToLog 'sc.exe create "${SERVICE}" binPath= "\"$INSTDIR\${APP_EXE}\"" start= delayed-auto obj= "${SERVICE_ACCOUNT}" DisplayName= "MediaRiffle"'
    Pop $0
    ${If} $0 != 0
      MessageBox MB_ICONSTOP "The MediaRiffle service couldn't be created (sc.exe exit $0)." /SD IDOK
      Abort
    ${EndIf}
  ${Else}
    nsExec::ExecToLog 'sc.exe config "${SERVICE}" binPath= "\"$INSTDIR\${APP_EXE}\""'
    Pop $0
  ${EndIf}
  nsExec::ExecToLog 'sc.exe description "${SERVICE}" "Streams the video library to browsers and Chromecasts."'
  Pop $0
  nsExec::ExecToLog 'sc.exe failure "${SERVICE}" reset= 86400 actions= restart/5000/restart/5000/restart/60000'
  Pop $0

  ; The Application event log source the service logs under: its own account can't create one
  WriteRegExpandStr HKLM "SYSTEM\CurrentControlSet\Services\EventLog\Application\${SERVICE}" "EventMessageFile" "%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\EventLogMessages.dll"
  WriteRegDWORD HKLM "SYSTEM\CurrentControlSet\Services\EventLog\Application\${SERVICE}" "TypesSupported" 7

  ; Settings and data: the service account may change its data folder and nothing else
  CreateDirectory "$APPDATA\MediaRiffle"
  IfFileExists "$APPDATA\MediaRiffle\appsettings.json" +3 0
    SetOutPath "$APPDATA\MediaRiffle"
    File "/oname=appsettings.json" "${CONFIG_TEMPLATE}"
  nsExec::ExecToLog 'icacls "$APPDATA\MediaRiffle" /grant "${SERVICE_ACCOUNT}:(OI)(CI)M"'
  Pop $0
  SetOutPath "$INSTDIR"

  WriteRegStr HKLM "${UNINST_KEY}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "${UNINST_KEY}" "Publisher" "${PUBLISHER}"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKLM "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINST_KEY}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegStr HKLM "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\uninstall.exe" /S'
  WriteRegStr HKLM "${UNINST_KEY}" "URLInfoAbout" "${DEFAULT_URL}"
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKLM "${UNINST_KEY}" "EstimatedSize" "$0"
SectionEnd

; Off by default: only needed when the reverse proxy runs on another machine (then Kestrel must listen on the LAN
; address, set in appsettings.json). Better still, limit the rule to the proxy's address afterwards.
Section /o "Allow TCP 5080 through Windows Firewall" SecFirewall
  nsExec::ExecToLog 'netsh advfirewall firewall delete rule name="MediaRiffle"'
  Pop $0
  nsExec::ExecToLog 'netsh advfirewall firewall add rule name="MediaRiffle" dir=in action=allow protocol=TCP localport=5080'
  Pop $0
SectionEnd

Section "-Start the service"
  nsExec::ExecToLog 'net start "${SERVICE}"'
  Pop $0
  ${If} $0 != 0
    MessageBox MB_ICONEXCLAMATION "The MediaRiffle service didn't start. Check %ProgramData%\MediaRiffle\appsettings.json and the Windows Application event log, then start it from Services." /SD IDOK
  ${EndIf}
SectionEnd

Section "Uninstall"
  !insertmacro StopService

  ; Run by a newer installer during an upgrade: leave the service, firewall rule and registration for it
  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/UPGRADE" $R1
  ${If} ${Errors}
    nsExec::ExecToLog 'sc.exe delete "${SERVICE}"'
    Pop $0
    nsExec::ExecToLog 'netsh advfirewall firewall delete rule name="MediaRiffle"'
    Pop $0
    DeleteRegKey HKLM "SYSTEM\CurrentControlSet\Services\EventLog\Application\${SERVICE}"
    DeleteRegKey HKLM "${UNINST_KEY}"
  ${EndIf}

  !insertmacro UninstallFiles
  Delete "$INSTDIR\uninstall.exe"
  RMDir "$INSTDIR"
  ; %ProgramData%\MediaRiffle (settings, accounts, watch positions) is kept, for a reinstall
SectionEnd
