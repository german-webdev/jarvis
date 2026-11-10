#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef ReleaseDir
  #error ReleaseDir is required
#endif
#ifndef AppVersion
  #define AppVersion "0.18.2"
#endif
[Setup]
#ifdef TestInstall
AppId=JARVIS-Package-Verification
AppName=JARVIS Package Test
AppMutex=JarvisPackageVerification
#else
AppId={{8732C110-19E3-4F58-BC12-C93DA868F504}
AppName=JARVIS
AppMutex=JarvisVoicePrototype
#endif
AppVersion={#AppVersion}
AppPublisher=JARVIS Project
AppPublisherURL=https://github.com/german-webdev/jarvis
DefaultDirName={localappdata}\Programs\JARVIS
DefaultGroupName=JARVIS
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
UninstallDisplayIcon={app}\Jarvis.exe
SetupIconFile=..\src\Jarvis.App\Assets\jarvis.ico
OutputDir={#ReleaseDir}
OutputBaseFilename=JARVIS-Setup-{#AppVersion}-win-x64
Compression=lzma2/fast
SolidCompression=yes
LZMANumBlockThreads=2
WizardStyle=modern
DisableProgramGroupPage=yes
DisableWelcomePage=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
CloseApplications=no
RestartApplications=no
SetupLogging=yes
UninstallLogMode=append
[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
[CustomMessages]
russian.UpdateTitle=Обновление JARVIS
english.UpdateTitle=Update JARVIS
russian.RepairTitle=Восстановление JARVIS
english.RepairTitle=Repair JARVIS
russian.UpdateInfo=Найдена версия %1. Будет установлена версия %2 поверх существующего приложения.%n%nПапка: %3%n%nНастройки, каталоги и подключения сохранятся. Перед продолжением полностью закройте JARVIS, включая значок в трее.
english.UpdateInfo=Version %1 is installed. Version %2 will replace the existing application.%n%nFolder: %3%n%nSettings, catalogs and connections will be preserved. Fully exit JARVIS, including the tray icon, before continuing.
russian.RepairInfo=JARVIS %1 уже установлен. Установщик восстановит файлы этой версии.%n%nПапка: %2%n%nНастройки, каталоги и подключения сохранятся. Перед продолжением полностью закройте JARVIS, включая значок в трее.
english.RepairInfo=JARVIS %1 is already installed. Setup will restore this version's application files.%n%nFolder: %2%n%nSettings, catalogs and connections will be preserved. Fully exit JARVIS, including the tray icon, before continuing.
russian.UpdateAction=Обновить
english.UpdateAction=Update
russian.RepairAction=Восстановить
english.RepairAction=Repair
russian.NewerInstalled=Уже установлена более новая версия JARVIS (%1). Используйте установщик этой или более новой версии.
english.NewerInstalled=A newer JARVIS version (%1) is already installed. Use an installer for that version or a newer one.
russian.Updated=Обновление JARVIS завершено
english.Updated=JARVIS updated
russian.Repaired=Восстановление JARVIS завершено
english.Repaired=JARVIS repaired
[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
#ifndef TestInstall
Name: "{userprograms}\JARVIS"; Filename: "{app}\Jarvis.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\JARVIS"; Filename: "{app}\Jarvis.exe"; WorkingDir: "{app}"; Tasks: desktopicon
#endif
[Run]
Filename: "{app}\Jarvis.exe"; Description: "{cm:LaunchProgram,JARVIS}"; Flags: nowait postinstall skipifsilent
[Code]
var
  ExistingDir, ExistingVersion, MaintenanceTitle, MaintenanceInfo, MaintenanceAction: String;
  Maintenance, RepairMode: Boolean;

function InitializeSetup(): Boolean;
var
  Key: String;
  OldVersion, NewVersion: Int64;
begin
  Result := True;
#ifdef TestInstall
  Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\JARVIS-Package-Verification_is1';
#else
  Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8732C110-19E3-4F58-BC12-C93DA868F504}_is1';
#endif
  Maintenance := RegQueryStringValue(HKCU64, Key, 'InstallLocation', ExistingDir);
  if not Maintenance then Maintenance := RegQueryStringValue(HKCU32, Key, 'InstallLocation', ExistingDir);
  if Maintenance then
  begin
    if not RegQueryStringValue(HKCU64, Key, 'DisplayVersion', ExistingVersion) then
      RegQueryStringValue(HKCU32, Key, 'DisplayVersion', ExistingVersion);
    Maintenance := ExistingDir <> '';
  end;
  if Maintenance then
  begin
    RepairMode := ExistingVersion = '{#AppVersion}';
    if StrToVersion(ExistingVersion, OldVersion) and StrToVersion('{#AppVersion}', NewVersion) then
      if ComparePackedVersion(OldVersion, NewVersion) > 0 then
      begin
        SuppressibleMsgBox(FmtMessage(CustomMessage('NewerInstalled'), [ExistingVersion]), mbError, MB_OK, IDOK);
        Result := False;
      end;
    Log('JARVIS maintenance: installed=' + ExistingVersion + '; target={#AppVersion}; directory=' + ExistingDir);
  end
  else Log('JARVIS maintenance: fresh installation');
end;

procedure InitializeWizard();
begin
  if Maintenance then
  begin
    WizardForm.DirEdit.Text := ExistingDir;
    if RepairMode then
    begin
      MaintenanceTitle := CustomMessage('RepairTitle');
      MaintenanceAction := CustomMessage('RepairAction');
      MaintenanceInfo := FmtMessage(CustomMessage('RepairInfo'), [ExistingVersion, ExistingDir]);
    end
    else
    begin
      MaintenanceTitle := CustomMessage('UpdateTitle');
      MaintenanceAction := CustomMessage('UpdateAction');
      MaintenanceInfo := FmtMessage(CustomMessage('UpdateInfo'), [ExistingVersion, '{#AppVersion}', ExistingDir]);
    end;
    WizardForm.WelcomeLabel1.Caption := MaintenanceTitle;
    WizardForm.WelcomeLabel2.Caption := MaintenanceInfo;
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := Maintenance and ((PageID = wpSelectDir) or (PageID = wpSelectProgramGroup) or (PageID = wpSelectTasks));
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if Maintenance then
  begin
    if CurPageID = wpReady then
    begin
      WizardForm.PageNameLabel.Caption := MaintenanceTitle;
      WizardForm.ReadyLabel.Caption := MaintenanceTitle;
      WizardForm.ReadyMemo.Lines.Text := MaintenanceInfo;
      WizardForm.NextButton.Caption := MaintenanceAction;
    end;
    if CurPageID = wpFinished then
      if RepairMode then WizardForm.FinishedHeadingLabel.Caption := CustomMessage('Repaired')
      else WizardForm.FinishedHeadingLabel.Caption := CustomMessage('Updated');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Command: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'JarvisVoiceAssistant', Command) then
      if Pos('"' + ExpandConstant('{app}\Jarvis.exe') + '"', Command) = 1 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'JarvisVoiceAssistant');
  end;
end;
