; Keyboard Assistant 安装脚本（Inno Setup 6）
;
; 设计决策：
; - PrivilegesRequired=lowest：每用户安装，装到 %LOCALAPPDATA%\Programs\Keyboard Assistant
;   （与 VS Code User Setup 同款位置）。不需要管理员权限，数据本就在 %APPDATA%。
; - AppId 固定 GUID：Inno 靠它识别"升级"而非"新装"，重装/升级时保留设置。
; - 自启用 [Tasks] + [Registry]：安装时默认勾选，用户可在向导里取消；
;   卸载时 Inno 会自动清掉本脚本创建的注册表项。
; - 卸载不删 %APPDATA%\XAssistant（用户的统计历史），与此前口径一致。

#define AppName "Keyboard Assistant"
#define AppVersion "1.3.4"
#define AppPublisher "YYiChen"
#define AppExe "KeyboardAssistant.exe"

[Setup]
AppId={{8F2A6C1D-4B7E-4E3A-9C5D-1A2B3C4D5E6F}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/YYiChen/Keyboard-Assistant
AppSupportURL=https://github.com/YYiChen/Keyboard-Assistant/releases
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=KeyboardAssistant-{#AppVersion}-setup
SetupIconFile=Assets\app-icon.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
; 覆盖安装时若程序在运行，先提示关闭
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\{#AppExe}

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："
Name: "autostart"; Description: "开机自动启动（可随时在程序设置里更改）"; GroupDescription: "附加任务："; Flags: checkedonce

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "记录键盘鼠标使用情况的本地工具"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Keyboard Assistant"; ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 停掉运行中的实例，避免文件占用导致卸载残留
Filename: "{cmd}"; Parameters: "/C taskkill /F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "KillApp"
