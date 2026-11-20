#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef WebView2Bootstrapper
  #error WebView2Bootstrapper is required
#endif

[Setup]
; Same AppId as the WinUI releases, so updates replace that install in place.
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
SetupIconFile=..\app\src-tauri\icons\icon.ico
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

[InstallDelete]
; The WinUI build shipped hundreds of runtime files; Mango is now a single executable.
; Settings and notes live in %LOCALAPPDATA%\Mango, not here, and are never touched.
Type: filesandordirs; Name: "{app}\*.dll"
Type: filesandordirs; Name: "{app}\*.pri"
Type: filesandordirs; Name: "{app}\*.xbf"
Type: filesandordirs; Name: "{app}\*.json"
Type: filesandordirs; Name: "{app}\*.winmd"
Type: filesandordirs; Name: "{app}\Read-SteamRoster.ps1"
Type: filesandordirs; Name: "{app}\Assets"
Type: filesandordirs; Name: "{app}\Views"
Type: filesandordirs; Name: "{app}\Resources"
Type: filesandordirs; Name: "{app}\Microsoft.UI.Xaml"
Type: filesandordirs; Name: "{app}\en-us"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#WebView2Bootstrapper}"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedsWebView2

[Icons]
Name: "{autoprograms}\Mango"; Filename: "{app}\Mango.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Mango"; Filename: "{app}\Mango.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Installing Microsoft Edge WebView2…"; Flags: waituntilterminated; Check: NeedsWebView2
Filename: "{app}\Mango.exe"; Description: "Launch Mango"; Flags: nowait postinstall skipifsilent
; In-app updates run silently with /RELAUNCH=1 and need Mango started again afterwards.
Filename: "{app}\Mango.exe"; Flags: nowait; Check: RelaunchAfterUpdate

[Code]
const
  WebView2Client = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function RelaunchAfterUpdate: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

function HasWebView2Version(RootKey: Integer; SubKey: String): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(RootKey, SubKey, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0');
end;

{ Mango's UI runs in the WebView2 runtime, which Windows 11 includes. }
function NeedsWebView2: Boolean;
begin
  Result := not (
    HasWebView2Version(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + WebView2Client) or
    HasWebView2Version(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WebView2Client) or
    HasWebView2Version(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\' + WebView2Client));
end;

[UninstallDelete]
Type: files; Name: "{app}\Assets\Fonts\radiance-regular.otf"
Type: files; Name: "{app}\Assets\Fonts\radiance-semibold.otf"
Type: dirifempty; Name: "{app}\Assets\Fonts"
