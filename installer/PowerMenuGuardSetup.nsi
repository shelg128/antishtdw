Unicode True

!define APP_NAME "Power Menu Guard"
!define APP_EXE "PowerMenuGuard.exe"
!define COMPANY_NAME "Local Utility"
!define APP_VERSION "0.1.0"
!define INSTALL_DIR "C:\Users\Admin\Documents\ANTI shtdw\app"
!define STARTMENU_DIR "$SMPROGRAMS\Power Menu Guard"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\PowerMenuGuard"
!define POLICY_KEY "Software\Microsoft\Windows\CurrentVersion\Policies\Explorer"
!define POLICY_VALUE "NoClose"

Name "${APP_NAME}"
OutFile "..\dist\Power Menu Guard Setup.exe"
InstallDir "${INSTALL_DIR}"
RequestExecutionLevel user
ShowInstDetails show
ShowUninstDetails show

Page directory
Page instfiles
UninstPage uninstConfirm
UninstPage instfiles

Section "Install"
  SetOverwrite on
  SetOutPath "$INSTDIR"
  CreateDirectory "$INSTDIR"
  File "..\dist\${APP_EXE}"
  File "..\dist\README.md"
  File "..\dist\Enable Power Menu Guard.cmd"
  File "..\dist\Disable Power Menu Guard.cmd"
  File "..\dist\Status Power Menu Guard.cmd"

  CreateDirectory "${STARTMENU_DIR}"
  CreateShortcut "${STARTMENU_DIR}\Power Menu Guard.lnk" "$INSTDIR\${APP_EXE}"
  CreateShortcut "${STARTMENU_DIR}\Enable Power Menu Guard.lnk" "$INSTDIR\${APP_EXE}" "--enable"
  CreateShortcut "${STARTMENU_DIR}\Disable Power Menu Guard.lnk" "$INSTDIR\${APP_EXE}" "--disable"
  CreateShortcut "${STARTMENU_DIR}\Status Power Menu Guard.lnk" "$INSTDIR\${APP_EXE}" "--status"
  CreateShortcut "${STARTMENU_DIR}\Uninstall Power Menu Guard.lnk" "$INSTDIR\Uninstall Power Menu Guard.exe"
  CreateShortcut "$DESKTOP\Power Menu Guard.lnk" "$INSTDIR\${APP_EXE}"

  WriteUninstaller "$INSTDIR\Uninstall Power Menu Guard.exe"

  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "${COMPANY_NAME}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" "$INSTDIR\Uninstall Power Menu Guard.exe"
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
SectionEnd

Section "Uninstall"
  ExecWait '"$INSTDIR\${APP_EXE}" --disable'
  DeleteRegValue HKCU "${POLICY_KEY}" "${POLICY_VALUE}"

  Delete "$DESKTOP\Power Menu Guard.lnk"
  Delete "${STARTMENU_DIR}\Power Menu Guard.lnk"
  Delete "${STARTMENU_DIR}\Enable Power Menu Guard.lnk"
  Delete "${STARTMENU_DIR}\Disable Power Menu Guard.lnk"
  Delete "${STARTMENU_DIR}\Status Power Menu Guard.lnk"
  Delete "${STARTMENU_DIR}\Uninstall Power Menu Guard.lnk"
  RMDir "${STARTMENU_DIR}"

  DeleteRegKey HKCU "${UNINSTALL_KEY}"

  Delete "$INSTDIR\Enable Power Menu Guard.cmd"
  Delete "$INSTDIR\Disable Power Menu Guard.cmd"
  Delete "$INSTDIR\Status Power Menu Guard.cmd"
  Delete "$INSTDIR\README.md"
  Delete "$INSTDIR\${APP_EXE}"
  Delete "$INSTDIR\Uninstall Power Menu Guard.exe"
  RMDir "$INSTDIR"
SectionEnd
