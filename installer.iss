[Setup]
AppName=TagEverything
AppVersion=1.2.1
AppVerName=TagEverything
DefaultDirName={autopf}\TagEverything
DefaultGroupName=TagEverything
OutputDir=.\Installer
OutputBaseFilename=TagEverything_Setup
SetupIconFile=TagEverything.UI\Assets\TagEverything.ico
UninstallDisplayIcon={app}\TagEverything.exe
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
var
  DeleteSettingsOnUninstall: Boolean;
  SettingsPath: String;

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

function InitializeUninstall: Boolean;
var
  LocalData: String;
  OptionsForm: TSetupForm;
  DeleteCheckBox: TNewCheckBox;
  PathLabel: TNewStaticText;
  ContinueButton, CancelButton: TNewButton;
begin
  Result := True;
  DeleteSettingsOnUninstall := False;
  if UninstallSilent then
    Exit;

  LocalData := ExpandConstant('{localappdata}');
  if (LocalData = '') or not DirExists(LocalData) then
    Exit;
  SettingsPath := AddBackslash(LocalData) + 'TagEverything';

#if VER >= EncodeVer(6, 6, 0)
  OptionsForm := CreateCustomForm(430, 136, True, True);
#else
  OptionsForm := CreateCustomForm;
  OptionsForm.ClientWidth := ScaleX(430);
  OptionsForm.ClientHeight := ScaleY(136);
#endif
  try
    OptionsForm.Caption := 'TagEverything 卸载选项';
    OptionsForm.BorderStyle := bsDialog;
    OptionsForm.Position := poScreenCenter;

    DeleteCheckBox := TNewCheckBox.Create(OptionsForm);
    DeleteCheckBox.Parent := OptionsForm;
    DeleteCheckBox.SetBounds(ScaleX(16), ScaleY(14), ScaleX(398), ScaleY(22));
    DeleteCheckBox.Caption := '删除配置文件';
    DeleteCheckBox.Checked := False;

    PathLabel := TNewStaticText.Create(OptionsForm);
    PathLabel.Parent := OptionsForm;
    PathLabel.AutoSize := False;
    PathLabel.WordWrap := True;
    PathLabel.SetBounds(ScaleX(16), ScaleY(42), ScaleX(398), ScaleY(44));
    PathLabel.Caption := '勾选后将删除以下目录及其中所有文件（包括 FFmpeg 缓存）：' + #13#10 + SettingsPath;

    ContinueButton := TNewButton.Create(OptionsForm);
    ContinueButton.Parent := OptionsForm;
    ContinueButton.SetBounds(ScaleX(218), ScaleY(96), ScaleX(92), ScaleY(28));
    ContinueButton.Caption := '继续卸载';
    ContinueButton.Default := True;
    ContinueButton.ModalResult := mrOk;

    CancelButton := TNewButton.Create(OptionsForm);
    CancelButton.Parent := OptionsForm;
    CancelButton.SetBounds(ScaleX(322), ScaleY(96), ScaleX(92), ScaleY(28));
    CancelButton.Caption := '取消';
    CancelButton.Cancel := True;
    CancelButton.ModalResult := mrCancel;

    Result := OptionsForm.ShowModal = mrOk;
    DeleteSettingsOnUninstall := Result and DeleteCheckBox.Checked;
  finally
    OptionsForm.Free;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep <> usPostUninstall) or not DeleteSettingsOnUninstall then
    Exit;
  if not DirExists(SettingsPath) then
    Exit;
  if not DelTree(SettingsPath, True, True, True) then
    MsgBox('部分配置文件无法删除，请手动检查：' + #13#10 + SettingsPath, mbError, MB_OK);
end;
