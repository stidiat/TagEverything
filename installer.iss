[Setup]
AppName=TagEverything
AppVersion=1.1.0
DefaultDirName={autopf}\TagEverything
DefaultGroupName=TagEverything
OutputDir=.\Installer
OutputBaseFilename=TagEverything_Setup
Compression=lzma
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
ChangesAssociations=yes
#ifdef SmokeTest
AppId=TagEverything-Isolated-Smoke
PrivilegesRequired=lowest
CreateUninstallRegKey=no
#endif

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
#ifndef SmokeTest
Name: "{group}\TagEverything"; Filename: "{app}\TagEverything.exe"
Name: "{autodesktop}\TagEverything"; Filename: "{app}\TagEverything.exe"; Tasks: desktopicon
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Registry]
#ifndef SmokeTest
Root: HKCR; Subkey: ".library"; ValueType: string; ValueName: ""; ValueData: "TagEverythingLibrary"; Flags: uninsdeletevalue
Root: HKCR; Subkey: "TagEverythingLibrary"; ValueType: string; ValueName: ""; ValueData: "TagEverything Library File"; Flags: uninsdeletekey
Root: HKCR; Subkey: "TagEverythingLibrary\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\TagEverything.exe,0"
Root: HKCR; Subkey: "TagEverythingLibrary\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\TagEverything.exe"" ""%1"""
#endif

[Run]
Filename: "{app}\TagEverything.exe"; Description: "{cm:LaunchProgram,TagEverything}"; Flags: nowait postinstall skipifsilent

[Code]
function HasDesktopRuntime: Boolean;
var
  Root: String;
  RuntimePath: String;
  FindRec: TFindRec;
begin
  Result := False;
  if not RegQueryStringValue(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Root) then
    Root := ExpandConstant('{pf64}\dotnet');
  RuntimePath := AddBackslash(Root) + 'shared\Microsoft.WindowsDesktop.App\';
  if FindFirst(RuntimePath + '9.*', FindRec) then
  begin
    try
      repeat
        if FileExists(RuntimePath + FindRec.Name + '\PresentationFramework.dll') then
        begin
          Result := True;
          Break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function InitializeSetup: Boolean;
begin
  Result := HasDesktopRuntime;
  if not Result then
    MsgBox('TagEverything requires .NET 9 Desktop Runtime (x64). Install it from https://dotnet.microsoft.com/download/dotnet/9.0 and run Setup again.', mbError, MB_OK);
end;
