#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif

[Setup]
AppId={{A54A8757-79EE-44B6-8943-43CF5C5B5FD2}
AppName=Mango
AppVersion={#AppVersion}
AppPublisher=Mango
AppPublisherURL=https://github.com/cteerakit/mango
AppSupportURL=https://github.com/cteerakit/mango/issues
AppUpdatesURL=https://github.com/cteerakit/mango/releases/latest
DefaultDirName={localappdata}\Programs\Mango
DefaultGroupName=Mango
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=MangoSetup-x64
SetupIconFile=..\src\Mango\Assets\AppIcon.ico
UninstallDisplayIcon={app}\Mango.exe
LicenseFile=..\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Mango"; Filename: "{app}\Mango.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Mango"; Filename: "{app}\Mango.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Mango.exe"; Description: "Launch Mango"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\Assets\Fonts\radiance-regular.otf"
Type: files; Name: "{app}\Assets\Fonts\radiance-semibold.otf"
Type: dirifempty; Name: "{app}\Assets\Fonts"
