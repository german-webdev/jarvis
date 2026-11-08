#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef ReleaseDir
  #error ReleaseDir is required
#endif
#define AppVersion "0.10.0"
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
CloseApplications=no
RestartApplications=no
SetupLogging=yes
UninstallLogMode=append
[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
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
