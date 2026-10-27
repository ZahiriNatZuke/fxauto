; FxAuto installer (Inno Setup 6). Build it with installer\build.ps1, which publishes the self-contained app into
; installer\staging and passes the version: ISCC /DAppVersion=1.0.0 /DStagingDir=... FxAuto.iss
;
; Per-user install, no administrator rights: FxAuto must not run elevated, or it could not drive FxSound.

#ifndef StagingDir
  #define StagingDir AddBackslash(SourcePath) + "staging"
#endif
#define AppExe "FxAuto.exe"
#ifndef AppVersion
  ; Fallback: the file version of the published exe, trimmed to major.minor.build.
  #define VerMajor
  #define VerMinor
  #define VerBuild
  #define VerRev
  #expr GetVersionComponents(StagingDir + "\" + AppExe, VerMajor, VerMinor, VerBuild, VerRev)
  #define AppVersion Str(VerMajor) + "." + Str(VerMinor) + "." + Str(VerBuild)
#endif

; Kernel object names used by FxAuto (src/FxAuto/App/Program.cs). They are an inter-process contract.
#define AppMutexName "Local\FxAuto-InstanciaUnica"
#define AppExitEventName "Local\FxAuto-Salir"
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"

[Setup]
; Fixed forever: Windows and Inno Setup recognise updates by this id. Never change it.
AppId={{7FCF2339-A73E-4786-8A35-E991C0E17642}
AppName=FxAuto
AppVersion={#AppVersion}
AppVerName=FxAuto {#AppVersion}
AppPublisher=Yohan González Almaguer
VersionInfoVersion={#AppVersion}
VersionInfoProductName=FxAuto
VersionInfoDescription=FxAuto Setup
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\FxAuto
DefaultGroupName=FxAuto
DisableProgramGroupPage=yes
DisableWelcomePage=no
DisableDirPage=auto
; Updates keep the tasks chosen at install time (desktop shortcut, start with Windows).
UsePreviousTasks=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
SetupIconFile=..\src\FxAuto\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=FxAuto
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir=Output
OutputBaseFilename=FxAuto-Setup-{#AppVersion}
; FxAuto is closed in code (see PrepareToInstall); the Restart Manager stays as a fallback.
CloseApplications=yes
RestartApplications=no
; Code signing (optional, no certificate yet). Without a signature Windows SmartScreen shows "Unknown publisher".
; To sign: define a sign tool in the IDE or on the command line, e.g.
;   ISCC "/Ssigntool=signtool.exe sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f cert.pfx /p PASS $f" FxAuto.iss
; and uncomment the two lines below (they also sign the uninstaller).
;SignTool=signtool
;SignedUninstaller=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[CustomMessages]
english.StartWithWindows=Start FxAuto with Windows
spanish.StartWithWindows=Iniciar FxAuto con Windows
english.LaunchFxAuto=Launch FxAuto
spanish.LaunchFxAuto=Abrir FxAuto
english.StartupGroup=Startup:
spanish.StartupGroup=Inicio:
english.ClosingFxAuto=Closing FxAuto...
spanish.ClosingFxAuto=Cerrando FxAuto...
english.StillRunning=FxAuto is still running. Close it from its tray icon (Exit) and try again.
spanish.StillRunning=FxAuto sigue abierto. Ciérralo desde su icono de la bandeja (Salir) y vuelve a intentarlo.
english.DeleteUserData=Do you also want to delete your FxAuto settings and logs?%n%n%1%n%2%n%nChoose No to keep them (recommended if you plan to install FxAuto again).
spanish.DeleteUserData=¿Quieres borrar también la configuración y los registros de FxAuto?%n%n%1%n%2%n%nElige No para conservarlos (recomendado si vas a volver a instalar FxAuto).

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "{cm:StartWithWindows}"; GroupDescription: "{cm:StartupGroup}"

[Files]
Source: "{#StagingDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\FxAuto"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\FxAuto"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchFxAuto}"; Flags: nowait postinstall skipifsilent
; Silent update (the in-app updater): start FxAuto again when it was running or /RELAUNCH was passed.
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: RestartAfterSilentUpdate

[Code]
const
  EVENT_MODIFY_STATE = $0002;

var
  WasRunning: Boolean;
  TasksDefaulted: Boolean;

function OpenEvent(dwDesiredAccess: DWORD; bInheritHandle: BOOL; lpName: string): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(hEvent: THandle): BOOL;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';

function IsFxAutoRunning(): Boolean;
begin
  Result := CheckForMutexes('{#AppMutexName}');
end;

{ Asks the running FxAuto to exit, like "FxAuto.exe --exit": runs the installed exe with --exit when there is one,
  otherwise sets the exit event directly (that is all --exit does). Waits up to 15 s for the instance to go away. }
function CloseFxAuto(ExePath: string): Boolean;
var
  ResultCode, I: Integer;
  H: THandle;
begin
  Result := True;
  if not IsFxAutoRunning() then Exit;
  if (ExePath <> '') and FileExists(ExePath) then
    Exec(ExePath, '--exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
  else
  begin
    H := OpenEvent(EVENT_MODIFY_STATE, False, '{#AppExitEventName}');
    if H <> 0 then
    begin
      SetEvent(H);
      CloseHandle(H);
    end;
  end;
  for I := 1 to 150 do
  begin
    if not IsFxAutoRunning() then
    begin
      Sleep(1000);  { the mutex is released just before the process ends: let it finish and free its files }
      Exit;
    end;
    Sleep(100);
  end;
  Result := not IsFxAutoRunning();
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  { "Start FxAuto with Windows" mirrors the current state on an update; it is checked on a first install. }
  if (CurPageID = wpSelectTasks) and not TasksDefaulted then
  begin
    TasksDefaulted := True;
    if RegValueExists(HKCU, '{#RunKey}', 'FxAuto') then
      WizardSelectTasks('startup')
    else if WizardForm.PrevAppDir <> '' then
      WizardSelectTasks('!startup');
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  WasRunning := IsFxAutoRunning();
  if WasRunning then
  begin
    WizardForm.PreparingLabel.Caption := CustomMessage('ClosingFxAuto');
    if not CloseFxAuto(ExpandConstant('{app}\{#AppExe}')) then
      Result := CustomMessage('StillRunning');
  end;
end;

function HasParam(const Name: string): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

function RestartAfterSilentUpdate(): Boolean;
begin
  Result := WizardSilent() and (WasRunning or HasParam('/RELAUNCH'));
end;

{ The HKCU Run value, exactly as FxAuto writes it (src/FxAuto/App/AutoStart.cs): "<path>\FxAuto.exe" --autostart. }
function RunCommand(): string;
begin
  Result := '"' + ExpandConstant('{app}\{#AppExe}') + '" --autostart';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then Exit;
  if WizardSilent() and (WizardForm.PrevAppDir <> '') then
  begin
    { Silent update: the user's current choice (made in FxAuto's settings) wins. Only fix the path of an existing value. }
    if RegValueExists(HKCU, '{#RunKey}', 'FxAuto') then
      RegWriteStringValue(HKCU, '{#RunKey}', 'FxAuto', RunCommand());
  end
  else if WizardIsTaskSelected('startup') then
    RegWriteStringValue(HKCU, '{#RunKey}', 'FxAuto', RunCommand())
  else
    { Task unticked: FxAuto must not start with Windows (also removes a value left by an older copy). }
    RegDeleteValue(HKCU, '{#RunKey}', 'FxAuto');
end;

function InitializeUninstall(): Boolean;
begin
  Result := CloseFxAuto(ExpandConstant('{app}\{#AppExe}'));
  if not Result and not UninstallSilent() then
    MsgBox(CustomMessage('StillRunning'), mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ConfigDir, LogDir: string;
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKCU, '{#RunKey}', 'FxAuto');
  if CurUninstallStep = usPostUninstall then
  begin
    ConfigDir := ExpandConstant('{userappdata}\FxAuto');
    LogDir := ExpandConstant('{localappdata}\FxAuto');
    if UninstallSilent() then Exit;
    if not DirExists(ConfigDir) and not DirExists(LogDir) then Exit;
    if SuppressibleMsgBox(FmtMessage(CustomMessage('DeleteUserData'), [ConfigDir, LogDir]),
                          mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
    begin
      DelTree(ConfigDir, True, True, True);
      DelTree(LogDir, True, True, True);
    end;
  end;
end;
