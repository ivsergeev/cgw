#define MyAppName "CorpGateway"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "CorpGateway"
#define MyAppURL "https://github.com/corpgateway"
#define MyAppExeName "CorpGateway.exe"
#define MyCliExeName "cgw.exe"

[Setup]
AppId={{B7E3F8A1-4D5C-4F2A-9B1E-6C8D7A3E5F10}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=CorpGateway-Setup-{#MyAppVersion}
SetupIconFile=..\CorpGateway\Assets\app.ico
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ChangesEnvironment=yes
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "addtopath"; Description: "Add cgw CLI to system PATH"; GroupDescription: "CLI Integration:"; Flags: checkedonce

[Files]
; Gateway application
Source: "publish\gateway\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
; CLI tool
Source: "publish\cli\*"; DestDir: "{app}\cli"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch CorpGateway"; Flags: nowait postinstall skipifsilent

[Code]
// --- PATH management ---
procedure AddToPath(Dir: string);
var
  Path: string;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Path) then
    Path := '';
  if Pos(Lowercase(Dir), Lowercase(Path)) = 0 then
  begin
    if Path <> '' then
      Path := Path + ';';
    Path := Path + Dir;
    RegWriteStringValue(HKCU, 'Environment', 'Path', Path);
  end;
end;

procedure RemoveFromPath(Dir: string);
var
  Path, LowerDir: string;
  P: Integer;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Path) then
    Exit;
  LowerDir := Lowercase(Dir);
  P := Pos(LowerDir, Lowercase(Path));
  if P > 0 then
  begin
    // Remove the directory and any trailing semicolon
    Delete(Path, P, Length(Dir));
    if (P <= Length(Path)) and (Path[P] = ';') then
      Delete(Path, P, 1)
    else if (P > 1) and (Path[P - 1] = ';') then
      Delete(Path, P - 1, 1);
    RegWriteStringValue(HKCU, 'Environment', 'Path', Path);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if IsTaskSelected('addtopath') then
      AddToPath(ExpandConstant('{app}\cli'));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RemoveFromPath(ExpandConstant('{app}\cli'));
    // Remove auto-start registry entry
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'CorpGateway');
  end;
end;
