; SPDX-License-Identifier: GPL-3.0-only
; EncoDeck installer (Inno Setup 7). Build with build\build-installer.ps1.
;
; Upgrade behaviour ("覆盖升级"):
;   * Fixed AppId, so a new version installs over the previous one (same directory, same tasks).
;   * Before installing, a running EncoDeck is asked to exit cleanly (EncoDeck.exe --exit), then the
;     previous version is uninstalled silently with /UPGRADE so no stale files remain.
;   * User data in %LOCALAPPDATA%\EncoDeck (settings, cache, logs, custom resources) is never touched
;     by an upgrade; a normal uninstall asks before deleting it.
;
; In-app update (Settings > Updates, see UpdateManager.cs): EncoDeck starts this installer with
;   /SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1
; and exits. /RELAUNCH=1 starts EncoDeck again (with --updated) once the silent install has finished.

#ifndef AppVersion
  #define AppVersion "0.5.0"
#endif
#ifndef AppVersionNumeric
  #define AppVersionNumeric "0.5.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\EncoDeck-win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif
#define AppName "EncoDeck"
#define AppExe "EncoDeck.exe"
#define AppIdGuid "48E531A0-79E2-4001-9F04-1F43A8C52FE9"

[Setup]
AppId={{{#AppIdGuid}}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Tianli
AppPublisherURL=https://github.com/TIANLI0/EncoDeck
AppSupportURL=https://github.com/TIANLI0/EncoDeck/issues
AppUpdatesURL=https://github.com/TIANLI0/EncoDeck/releases
AppCopyright=Copyright (C) 2026 Tianli (GPL-3.0-only)
VersionInfoVersion={#AppVersionNumeric}
VersionInfoProductVersion={#AppVersionNumeric}
VersionInfoDescription={#AppName} Setup
; Per-user install by default (no administrator rights needed); %LOCALAPPDATA%\Programs\EncoDeck.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes
UsePreviousTasks=yes
UsePreviousLanguage=yes
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=EncoDeck-Setup-{#AppVersion}-win-x64
SetupIconFile=..\src\EncoDeck.App\Assets\EncoDeck.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=auto

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[CustomMessages]
chinesesimplified.TaskDesktopIcon=创建桌面快捷方式
chinesesimplified.TaskAutostart=开机时在后台启动 EncoDeck
chinesesimplified.TaskGroupOptions=其他选项：
chinesesimplified.LaunchApp=启动 EncoDeck
chinesesimplified.DeleteUserData=是否同时删除 EncoDeck 的设置、日志、缓存和自定义资源？%n%n（位于 %1）
chinesesimplified.UnofficialNote=EncoDeck 是非官方社区项目，与 OPPO、OnePlus、realme 无任何官方关系。
english.TaskDesktopIcon=Create a desktop shortcut
english.TaskAutostart=Start EncoDeck in the background when Windows starts
english.TaskGroupOptions=Additional options:
english.LaunchApp=Launch EncoDeck
english.DeleteUserData=Also delete EncoDeck's settings, logs, cache and custom resources?%n%n(located in %1)
english.UnofficialNote=EncoDeck is an unofficial community project, not affiliated with OPPO, OnePlus or realme.
japanese.TaskDesktopIcon=デスクトップにショートカットを作成する
japanese.TaskAutostart=Windows の起動時に EncoDeck をバックグラウンドで起動する
japanese.TaskGroupOptions=その他のオプション:
japanese.LaunchApp=EncoDeck を起動する
japanese.DeleteUserData=EncoDeck の設定、ログ、キャッシュ、カスタムリソースも削除しますか？%n%n（場所: %1）
japanese.UnofficialNote=EncoDeck は非公式のコミュニティプロジェクトであり、OPPO、OnePlus、realme とは一切関係ありません。

[Tasks]
Name: "desktopicon"; Description: "{cm:TaskDesktopIcon}"; GroupDescription: "{cm:TaskGroupOptions}"; Flags: unchecked
Name: "autostart"; Description: "{cm:TaskAutostart}"; GroupDescription: "{cm:TaskGroupOptions}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
; Same value the app writes from Settings → Start with Windows (see StartupRegistration.cs).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"" --background"; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent; Check: not IsRelaunchRequested
; In-app update: not a postinstall entry, so it also runs in silent mode, but only when EncoDeck asked for it.
Filename: "{app}\{#AppExe}"; Parameters: "--updated"; Flags: nowait runasoriginaluser; Check: IsRelaunchRequested

[UninstallRun]
Filename: "{app}\{#AppExe}"; Parameters: "--exit"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "StopEncoDeck"

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{' + '{#AppIdGuid}' + '}_is1';

function IsUpgradeUninstall(): Boolean;
begin
  Result := ExpandConstant('{param:UPGRADE|0}') = '1';
end;

// /RELAUNCH=1: started by EncoDeck's in-app update, which exited and expects to be started again.
function IsRelaunchRequested(): Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

function GetPreviousUninstaller(): String;
var
  Value: String;
begin
  Result := '';
  if RegQueryStringValue(HKCU, UninstallKey, 'UninstallString', Value) or
     RegQueryStringValue(HKLM, UninstallKey, 'UninstallString', Value) then
    Result := RemoveQuotes(Value);
end;

function GetPreviousAppDir(): String;
var
  Value: String;
begin
  Result := '';
  if RegQueryStringValue(HKCU, UninstallKey, 'InstallLocation', Value) or
     RegQueryStringValue(HKLM, UninstallKey, 'InstallLocation', Value) then
    Result := RemoveBackslashUnlessRoot(Value);
end;

procedure StopRunningApp(const Dir: String);
var
  ResultCode: Integer;
begin
  // Ask a running instance to shut down cleanly (removes its tray icon, closes the control channel).
  if (Dir <> '') and FileExists(AddBackslash(Dir) + '{#AppExe}') then
    Exec(AddBackslash(Dir) + '{#AppExe}', '--exit', Dir, SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Uninstaller: String;
  ResultCode: Integer;
begin
  Result := '';
  StopRunningApp(GetPreviousAppDir());
  Uninstaller := GetPreviousUninstaller();
  if (Uninstaller <> '') and FileExists(Uninstaller) then
  begin
    // Remove the previous version's files so no stale binaries remain. User data is kept.
    Exec(Uninstaller, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPGRADE=1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

// Labs "Audio codec from Windows" registered a boot-time trace session (WindowsA2dpCodecLog.cs): unregister it and
// delete its log, which needs the administrator prompt the user gave when authorizing.
procedure RemoveCodecLog();
var
  LogDir: String;
  ErrorCode: Integer;
begin
  LogDir := ExpandConstant('{commonappdata}\{#AppName}\Codec');
  if not DirExists(LogDir) or UninstallSilent() then Exit;
  ShellExec('runas', ExpandConstant('{sys}\cmd.exe'),
    '/d /s /c "logman stop EncoDeck-BthA2dp -ets & logman delete "autosession\EncoDeck-BthA2dp" & rmdir /s /q "' + LogDir + '""',
    '', SW_HIDE, ewWaitUntilTerminated, ErrorCode);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  if IsUpgradeUninstall() then Exit;

  // A real uninstall: remove the autostart entry the app may have created itself.
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#AppName}');
  RemoveCodecLog();

  DataDir := ExpandConstant('{localappdata}\{#AppName}');
  if DirExists(DataDir) and not UninstallSilent() then
  begin
    if MsgBox(FmtMessage(CustomMessage('DeleteUserData'), [DataDir]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(DataDir, True, True, True);
  end;
end;
