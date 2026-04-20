; ============================================================
; Systema Optimization Suite — Inno Setup Script
; ============================================================

#define MyAppName "Systema"
#define MyAppVersion "1.7.47"
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
Compression=lzma2/fast
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
; Version info embedded in setup EXE — improves SmartScreen reputation scoring
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppDescription}
VersionInfoCopyright=© 2026 {#MyAppPublisher}
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
; Manual install — shows "Launch Systema" checkbox on final wizard page
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; \
  Flags: nowait postinstall skipifsilent runascurrentuser
; Silent/auto-update — relaunches into tray (Ghost Mode), no window popup
Filename: "{app}\{#MyAppExeName}"; Parameters: "--silent"; Flags: nowait runascurrentuser shellexec; \
  Check: WizardSilent
; Remove "downloaded from internet" Zone.Identifier from ALL files (app + subfolders + DLLs)
; This prevents Windows Defender and SmartScreen from blocking the unsigned exe on subsequent launches
Filename: "powershell.exe"; Parameters: "-NoProfile -Command ""$ErrorActionPreference='SilentlyContinue'; Get-ChildItem -Path '{app}' -Recurse -File | Unblock-File; Write-Host 'Files unblocked'; exit 0"""; \
  Flags: runhidden waituntilterminated; StatusMsg: "Unblocking files from internet restriction..."

; Add Defender exclusion for the entire installation directory (prevents real-time scan interference)
; This is crucial for unsigned elevated apps to avoid false positives
Filename: "powershell.exe"; Parameters: "-NoProfile -Command ""$ErrorActionPreference='SilentlyContinue'; Add-MpPreference -ExclusionPath '{app}' -Force; Write-Host 'Defender exclusion added'; exit 0"""; \
  Flags: runhidden waituntilterminated; StatusMsg: "Adding Windows Defender exclusion..."

[Code]
// Check Windows version and display SmartScreen info (only in interactive mode)
function InitializeSetup(): Boolean;
var
  Version: TWindowsVersion;
  SmartScreenMsg: String;
begin
  GetWindowsVersionEx(Version);
  if (Version.Major < 10) then
  begin
    MsgBox('Systema requires Windows 10 or later.', mbError, MB_OK);
    Result := False;
  end
  else
  begin
    // ONLY show SmartScreen warning in INTERACTIVE mode (not during auto-update /VERYSILENT installs)
    // Silent updates use /SUPPRESSMSGBOXES /VERYSILENT so this won't block them
    if not WizardSilent then
    begin
      SmartScreenMsg := 'UNSIGNED EXECUTABLE NOTICE' + #10#10 +
        'Systema is intentionally unsigned for transparency and open-source integrity.' + #10 +
        'Windows SmartScreen may block it on first download.' + #10#10 +
        'HOW TO BYPASS SMARTSCREEN:' + #10 +
        '1. Download the installer' + #10 +
        '2. When SmartScreen appears, click "More info"' + #10 +
        '3. Click "Run anyway"' + #10#10 +
        'WHY UNSIGNED?' + #10 +
        'Code signing requires closed CA integration and remote servers, ' +
        'which conflicts with our open-source, self-contained design.' + #10#10 +
        'SAFETY CHECK:' + #10 +
        'Always download from: https://github.com/Coolbright27/Systema/releases' + #10#10 +
        'Click OK to continue installation.';
      MsgBox(SmartScreenMsg, mbInformation, MB_OK);
    end;
    Result := True;
  end;
end;
