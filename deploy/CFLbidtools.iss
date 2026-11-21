#ifndef PublishDir
#error PublishDir is required
#endif
#ifndef PackageDir
#error PackageDir is required
#endif

[Setup]
AppId=CFLbidtools
AppName=CFL Engineering Workbench
AppVersion=1.2.14
AppPublisher=CFL
DefaultDirName={localappdata}\Programs\CFLbidtools
DefaultGroupName=CFL Engineering Workbench
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#PackageDir}
OutputBaseFilename=CFLbidtools-Windows-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\wwwroot\favicon.ico
WizardSmallImageFile=Branding\cfl-wizard.bmp
UninstallDisplayIcon={app}\CflWorkbench.exe

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "Connect-Phone.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\CFL Engineering Workbench"; Filename: "{app}\CflWorkbench.exe"; Parameters: "--open-browser"; WorkingDir: "{app}"
Name: "{autoprograms}\CFL Workbench - Phone Connection"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy RemoteSigned -File ""{app}\Connect-Phone.ps1"""; WorkingDir: "{app}"; IconFilename: "{app}\CflWorkbench.exe"

[Run]
Filename: "{app}\CflWorkbench.exe"; Parameters: "--open-browser"; Description: "Open CFL Engineering Workbench"; Flags: nowait postinstall skipifsilent
