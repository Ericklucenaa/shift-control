Unicode True
!include "MUI2.nsh"
!include "FileFunc.nsh"
Var UpdateMode
Var PortableMode
Var NoRestart
!ifndef APP_VERSION
!define APP_VERSION "0.1.0"
!endif
!ifndef PUBLISH_DIR
!error "Defina PUBLISH_DIR com a pasta publicada."
!endif
!ifndef OUTPUT_FILE
!define OUTPUT_FILE "ShiftControl-${APP_VERSION}-setup.exe"
!endif
Name "Shift Control"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\ShiftControl"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\ShiftControl.exe"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "PortugueseBR"

!macro WaitForApp PREFIX
  StrCpy $1 0
  ${PREFIX}wait:
    System::Call 'kernel32::OpenMutexW(i 0x100000, i 0, w "Local\ShiftControl.SingleInstance") p.r0'
    IntCmp $0 0 ${PREFIX}ready
    System::Call 'kernel32::CloseHandle(p r0)'
    IntOp $1 $1 + 1
    IntCmp $1 30 ${PREFIX}blocked
    Sleep 1000
    Goto ${PREFIX}wait
  ${PREFIX}blocked:
    IfSilent 0 +3
      SetErrorLevel 2
      Quit
    MessageBox MB_OK|MB_ICONEXCLAMATION "Feche o Shift Control pelo menu Sair da bandeja e tente novamente."
    Abort
  ${PREFIX}ready:
!macroend

Function .onInit
  SetShellVarContext current
  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/UPDATE" $R1
  IfErrors +2
    StrCpy $UpdateMode 1
  ClearErrors
  ${GetOptions} $R0 "/PORTABLE" $R1
  IfErrors +2
    StrCpy $PortableMode 1
  ClearErrors
  ${GetOptions} $R0 "/NORESTART" $R1
  IfErrors +2
    StrCpy $NoRestart 1
  ; Wait for the exact caller even for portable copies, which use no global mutex.
  ClearErrors
  ${GetOptions} $R0 "/WAITPID=" $R1
  IfErrors caller_ready
  System::Call 'kernel32::OpenProcess(i 0x100000, i 0, i R1) p.r0'
  IntCmp $0 0 caller_ready
  System::Call 'kernel32::WaitForSingleObject(p r0, i 30000) i.r1'
  System::Call 'kernel32::CloseHandle(p r0)'
  IntCmp $1 0 caller_ready
  SetErrorLevel 2
  Quit
  caller_ready:
  StrCmp $PortableMode 1 skip_wait
  !insertmacro WaitForApp "install_"
  skip_wait:
FunctionEnd

Function .onInstSuccess
  StrCmp $UpdateMode 1 0 done
  StrCmp $NoRestart 1 done
  Exec '"$INSTDIR\ShiftControl.exe"'
  done:
FunctionEnd

Section "Shift Control"
  SetOutPath "$INSTDIR"
  File /r "${PUBLISH_DIR}\*.*"
  StrCmp $PortableMode 1 portable_done
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\Shift Control"
  CreateShortcut "$SMPROGRAMS\Shift Control\Shift Control.lnk" "$INSTDIR\ShiftControl.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ShiftControl" "DisplayName" "Shift Control"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ShiftControl" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ShiftControl" "Publisher" "Shift"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ShiftControl" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ShiftControl" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ShiftControl" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ShiftControl" "NoRepair" 1
  portable_done:
SectionEnd

Function un.onInit
  SetShellVarContext current
  !insertmacro WaitForApp "uninstall_"
FunctionEnd

Section "Uninstall"
  ReadEnvStr $0 "USERNAME"
  nsExec::ExecToLog 'schtasks /Delete /TN "ShiftControl-$0" /F'
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ShiftControl"
  ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "ShiftControl"
  StrCpy $1 '$\"$INSTDIR\ShiftControl.exe$\"'
  StrLen $2 $1
  StrCpy $3 $0 $2
  StrCmp $3 $1 0 +2
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "ShiftControl"
  Delete "$SMPROGRAMS\Shift Control\Shift Control.lnk"
  RMDir "$SMPROGRAMS\Shift Control"
  ; Diretório fixo por usuário. Preferências e histórico em LocalAppData\ShiftControl são preservados.
  RMDir /r "$INSTDIR"
SectionEnd
