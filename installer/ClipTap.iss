#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif

[Setup]
#ifdef InstallerTest
AppId=ClipTap.InstallerTest
AppName=ClipTap Installer Test
OutputBaseFilename=ClipTap-test-setup
#else
AppId={{61CC60C4-B2D1-47C9-86C0-90B59359839B}
AppName=ClipTap
OutputBaseFilename=ClipTap-win-x64-setup
AppMutex=Local\ClipTap.{username}
#endif
AppVersion={#AppVersion}
AppPublisher=langji3
AppPublisherURL=https://github.com/langji3/cliptap
AppSupportURL=https://github.com/langji3/cliptap/issues
AppUpdatesURL=https://github.com/langji3/cliptap/releases
DefaultDirName={localappdata}\Programs\ClipTap
DisableWelcomePage=no
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
WizardSizePercent=110
SetupIconFile=..\src\ClipTap\Assets\cliptap.ico
UninstallDisplayIcon={app}\ClipTap.exe
UninstallDisplayName=ClipTap
OutputDir={#OutputDir}
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoDescription=ClipTap Setup

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Run]
#ifndef InstallerTest
Filename: "{app}\ClipTap.exe"; Parameters: "--background"; Description: "{cm:RunAfterInstall}"; Flags: nowait postinstall skipifsilent
#endif

[CustomMessages]
chinesesimplified.RunAfterInstall=安装后运行 ClipTap（驻留托盘）
english.RunAfterInstall=Run ClipTap after installation (system tray)

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ClipTap', Command) then
      if CompareText(Command, '"' + ExpandConstant('{app}\ClipTap.exe') + '" --background') = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ClipTap');
  end;
end;
