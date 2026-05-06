; ============================================================
; Systema Optimization Suite - Inno Setup Script
; ============================================================

#define MyAppName "Systema"
#define MyAppVersion "1.7.72"
#define MyAppPublisher "Systema"
#define MyAppURL "https://github.com/systema-app"
#define MyAppExeName "Systema.exe"
#define MyAppDescription "High-performance Windows optimization suite"
#define PublishDir "..\publish"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
LicenseFile=
OutputDir=.\output
OutputBaseFilename=Systema_Setup_{#MyAppVersion}
SetupIconFile=..\src\Systema\Assets\logo.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
MinVersion=10.0.17763
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
WizardResizable=yes
; Version info embedded in setup EXE - improves SmartScreen reputation scoring
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppDescription}
VersionInfoCopyright=Copyright 2026 {#MyAppPublisher}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "quicklaunchicon"; Description: "{cm:CreateQuickLaunchIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked; OnlyBelowVersion: 6.1; Check: not IsAdminInstallMode

[Files]
; Main executable and all runtime files
Source: "{#PublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Manual install - shows "Launch Systema" checkbox on final wizard page
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; \
  Flags: nowait postinstall skipifsilent runascurrentuser
; Silent/auto-update - relaunches into tray (Ghost Mode), no window popup
Filename: "{app}\{#MyAppExeName}"; Parameters: "--silent"; Flags: nowait runascurrentuser shellexec; \
  Check: WizardSilent

[UninstallRun]
; Restore all Windows settings Systema may have changed before files are deleted.
; runhidden           = no window popup during uninstall
; waituntilterminated = block the uninstaller until cleanup completes
; runascurrentuser    = runs as the interactive user (needed for HKCU access)
Filename: "{app}\{#MyAppExeName}"; Parameters: "--cleanup"; RunOnceId: "SystemaCleanup"; \
  Flags: runhidden waituntilterminated runascurrentuser

[Code]
// Check Windows version. Minimum supported: Windows 10.
// A friendly welcome notice is shown in interactive mode so users who hit a
// Windows protection prompt know how to proceed. Silent auto-updates skip this.
function InitializeSetup(): Boolean;
var
  Version: TWindowsVersion;
  WelcomeMsg: String;
begin
  GetWindowsVersionEx(Version);
  if (Version.Major < 10) then
  begin
    MsgBox('Systema requires Windows 10 or later.', mbError, MB_OK);
    Result := False;
  end
  else
  begin
    // Interactive install only - auto-updates run with /VERYSILENT and skip this.
    if not WizardSilent then
    begin
      WelcomeMsg := 'Welcome to Systema Setup' + #10#10 +
        'Systema is a free, open-source Windows optimization tool.' + #10#10 +
        'If Windows shows a protection prompt during install:' + #10 +
        '  1. Click More info' + #10 +
        '  2. Click Run anyway' + #10#10 +
        'Always download from the official GitHub releases page:' + #10 +
        'https://github.com/Coolbright27/Systema/releases' + #10#10 +
        'Click OK to continue.';
      MsgBox(WelcomeMsg, mbInformation, MB_OK);
    end;
    Result := True;
  end;
end;
