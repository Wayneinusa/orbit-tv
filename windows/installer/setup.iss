; Installer for Superior IPTV Player (Inno Setup 6).
; Installs for the current user only, so it needs no administrator password.
#define AppName "Superior IPTV Player"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{6B3E2C55-9D1F-4A7B-8E21-5C0F7A9D4B10}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Superior IPTV Player
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=SuperiorIPTVPlayer-Setup
SetupIconFile=..\src\App\app.ico
UninstallDisplayIcon={app}\SuperiorIPTVPlayer.exe
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\SuperiorIPTVPlayer.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\SuperiorIPTVPlayer.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SuperiorIPTVPlayer.exe"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsWin64 then
  begin
    MsgBox('Superior IPTV Player needs a 64-bit version of Windows 10 or Windows 11.', mbError, MB_OK);
    Result := False;
  end;
end;
