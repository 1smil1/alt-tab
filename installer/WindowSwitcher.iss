; Window Switcher 标准安装包 (Inno Setup 6)
; 构建: installer\build-installer.cmd  (先 dotnet publish 单文件到 installer\publish\)
#define MyAppName "alt-tab"
#define MyAppVersion "1.0.0"
#define MyAppExe "WindowSwitcherWpf.exe"

[Setup]
AppId={{8F3A2C64-9D1E-4E5B-A7C8-2B6D9E0F4A21}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExe}
; 用户级安装 (无需管理员): 默认 %LOCALAPPDATA%\Programs\WindowSwitcher,
; config.json 便携设计 (exe 同目录) 保持可写. 安装向导可自选路径.
PrivilegesRequired=lowest
DefaultDirName={autopf}\WindowSwitcher
WizardStyle=modern
DisableProgramGroupPage=yes
DefaultGroupName={#MyAppName}
CloseApplications=no
Compression=lzma2/max
SolidCompression=yes
OutputDir=Output
OutputBaseFilename=alt-tab-setup-{#MyAppVersion}
SetupIconFile=..\src\WindowSwitcherWpf\appicon.ico

[Languages]
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinese.TaskGroup=附加任务:
english.TaskGroup=Additional tasks:
chinese.TaskDesk=创建桌面快捷方式
english.TaskDesk=Create a &desktop shortcut
chinese.TaskAuto=开机自动启动 (托盘常驻)
english.TaskAuto=Start with Windows (tray resident)

[Files]
Source: "publish\{#MyAppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Tasks]
Name: "desktopicon"; Description: "{cm:TaskDesk}"; GroupDescription: "{cm:TaskGroup}"
Name: "autostart"; Description: "{cm:TaskAuto}"; GroupDescription: "{cm:TaskGroup}"; Flags: unchecked

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "WindowSwitcherWpf"; ValueData: """{app}\{#MyAppExe}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExe}"; WorkingDir: {app}; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#MyAppExe} /F"; Flags: runhidden; RunOnceId: "KillApp"

[Code]
procedure KillApp;
var RC: Integer;
begin
  Exec(ExpandConstant('{cmd}'), '/C taskkill /IM {#MyAppExe} /F', '', SW_HIDE, ewWaitUntilTerminated, RC);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then KillApp;  // 覆盖安装前结束常驻进程
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then KillApp;  // 卸载前结束常驻进程
end;
