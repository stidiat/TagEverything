[Setup]
AppName=TagEverything
AppVersion=1.0
DefaultDirName={autopf}\TagEverything
DefaultGroupName=TagEverything
OutputDir=.\Installer
OutputBaseFilename=TagEverything_Setup
Compression=lzma
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
ChangesAssociations=yes

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\TagEverything"; Filename: "{app}\TagEverything.UI.exe"
Name: "{autodesktop}\TagEverything"; Filename: "{app}\TagEverything.UI.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Registry]
Root: HKCR; Subkey: ".library"; ValueType: string; ValueName: ""; ValueData: "TagEverythingLibrary"; Flags: uninsdeletevalue
Root: HKCR; Subkey: "TagEverythingLibrary"; ValueType: string; ValueName: ""; ValueData: "TagEverything Library File"; Flags: uninsdeletekey
Root: HKCR; Subkey: "TagEverythingLibrary\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\TagEverything.UI.exe,0"
Root: HKCR; Subkey: "TagEverythingLibrary\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\TagEverything.UI.exe"" ""%1"""

[Run]
Filename: "{app}\TagEverything.UI.exe"; Description: "{cm:LaunchProgram,TagEverything}"; Flags: nowait postinstall skipifsilent
