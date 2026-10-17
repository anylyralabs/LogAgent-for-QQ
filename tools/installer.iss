; LogAgent Windows 安装器脚本（Inno Setup 6+）
; 用法：发布后运行 Inno Setup 编译本脚本 → 生成 LogAgent-Setup-x64.exe
;   1. dotnet publish src/LogAgent.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/win-x64
;   2. 用 Inno Setup Compiler (ISCC.exe) 编译本文件
; 安装模式：per-user（免管理员权限，装到 %LocalAppData%\Programs\LogAgent）

#define MyAppName "LogAgent"
#define MyAppVersion "0.7.0"
#define MyAppPublisher "logagent-for-qq"
#define MyAppExeName "LogAgent.App.exe"

[Setup]
AppId={{8E7A4C21-5B3F-4D9E-9C1A-A1B2C3D4E5F6}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\publish\installer
OutputBaseFilename=LogAgent-Setup-x64-{#MyAppVersion}
SetupIconFile=..\src\LogAgent.App\Assets\logo.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："
Name: "autostart"; Description: "开机自动启动（登录后后台运行并自动连接 Bot）"; GroupDescription: "附加任务："

[Files]
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; 开机自启动（HKCU Run，带 --minimized 后台启动）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "LogAgent"; ValueData: """{app}\{#MyAppExeName}"" --minimized"; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 卸载时不删除用户数据（%AppData%\LogAgent），保留会话/知识库/配置
Type: files; Name: "{app}\LogAgent.App.pdb"
