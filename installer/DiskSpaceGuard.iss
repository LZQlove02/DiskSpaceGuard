; DiskSpaceGuard 安装脚本（Inno Setup 6）
; 构建：installer\build-release.ps1
; 说明：应用自身仍以 asInvoker 运行；安装/卸载需要管理员权限以写入 Program Files。

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\dist\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{7C4A2F3B-9D1E-4A6B-8F2C-3D5E7A9B1C0F}
AppName=DiskSpaceGuard
AppVersion={#MyAppVersion}
AppVerName=DiskSpaceGuard {#MyAppVersion}
AppPublisher=DiskSpaceGuard
DefaultDirName={autopf}\DiskSpaceGuard
DefaultGroupName=DiskSpaceGuard
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=DiskSpaceGuard-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\DiskSpaceGuard.exe
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany=DiskSpaceGuard
VersionInfoDescription=DiskSpaceGuard installer
VersionInfoProductName=DiskSpaceGuard

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\DiskSpaceGuard"; Filename: "{app}\DiskSpaceGuard.exe"
Name: "{group}\卸载 DiskSpaceGuard"; Filename: "{uninstallexe}"
Name: "{autodesktop}\DiskSpaceGuard"; Filename: "{app}\DiskSpaceGuard.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\DiskSpaceGuard.exe"; Description: "{cm:LaunchProgram,DiskSpaceGuard}"; Flags: nowait postinstall skipifsilent
