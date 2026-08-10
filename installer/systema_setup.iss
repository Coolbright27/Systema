; ============================================================
; Systema Optimization Suite - Inno Setup Script
; ============================================================

#define MyAppName "Systema"
#define MyAppVersion "0.7.279"
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
OutputDir=.\Final
OutputBaseFilename=Systema_Setup_{#MyAppVersion}
SetupIconFile=..\src\Systema\Assets\logo.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
; Force-close any running Systema before copying files. Systema relaunches itself
; (launch-on-startup), so without this the running Systema.exe stays locked and the
; installer silently skips overwriting it — leaving the user on the old version.
CloseApplications=force
RestartApplications=no
CloseApplicationsFilter=*.exe,*.dll
MinVersion=10.0.17763
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
WizardResizable=yes
; We intentionally write one HKCU value (DOTNET_BUNDLE_EXTRACT_BASE_DIR) from
; this admin installer. Inno Setup correctly maps the write to the original
; interactive user's hive — that is the desired behaviour. Suppress the warning.
UsedUserAreasWarning=no
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

[Registry]
; Redirect .NET 8 single-file bundle extraction from %LOCALAPPDATA%\Temp\.net\
; into {app}\runtime. This keeps every file Systema touches inside one tidy
; folder. The DLLs that .NET unpacks there at first launch (System.Windows.*,
; the WPF native libs, etc.) are all Microsoft Authenticode-signed — they keep
; their signatures through single-file extraction, so SmartScreen / SAC ignore
; them. Only Systema.exe itself is unsigned, and it's a single bundled file.
;
; HKCU\Environment is the standard "per-user environment variable" location;
; Windows applies it for every process the user starts going forward.
Root: HKCU; Subkey: "Environment"; ValueType: string; ValueName: "DOTNET_BUNDLE_EXTRACT_BASE_DIR"; ValueData: "{app}\runtime"; Flags: preservestringtype uninsdeletevalue

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
// ════════════════════════════════════════════════════════════════════════════
//  Why this [Code] section is deliberately minimal
// ════════════════════════════════════════════════════════════════════════════
//
// Earlier versions of this installer ran six hidden `powershell.exe` processes
// with `-ExecutionPolicy Bypass`, calling `Add-MpPreference` to add Systema to
// Windows Defender's exclusion list and `Unblock-File` to strip Zone.Identifier
// ADS from the extracted files.
//
// That was a mistake. An UNSIGNED installer spawning hidden PowerShell to
// exclude itself from Defender is the single most recognisable malware
// behavioural signature there is — Defender's behavioural engine flags the
// PATTERN regardless of what the script actually does. The "mitigations" were
// the thing getting the installer blocked.
//
// They were also unnecessary:
//   • Unblock-File:  Inno Setup writes freshly-created files into {app}. Those
//                    files do NOT inherit the Zone.Identifier ADS — only the
//                    downloaded Systema_Setup.exe itself carries it. We were
//                    unblocking files that were never blocked.
//   • ExclusionPath: The real SAC problem (a separate unsigned Systema.dll)
//                    was already solved by single-file publish. Everything is
//                    bundled inside Systema.exe; the .NET runtime DLLs that
//                    get extracted to {app}\runtime are Microsoft-signed.
//   • ExclusionProcess: same — self-excluding from an unsigned installer is
//                    the trigger, not the cure.
//
// The installer is now a plain file-copy installer. The ONE remaining first-run
// hurdle — the SmartScreen "Windows protected your PC" prompt on the unsigned
// Systema_Setup.exe itself — is unavoidable without a code-signing certificate
// and is a one-time "More info → Run anyway" click. The welcome message below
// tells the user exactly that.
//
// If a user's Defender ever flags the RUNNING app's behaviour (Task Sleep's
// working-set trimming, the sc.exe service calls, etc.), the correct fix is for
// the USER to add an exclusion manually from their own elevated PowerShell —
// NOT for our unsigned binary to do it for them.
// ════════════════════════════════════════════════════════════════════════════

// NOTE ON A DARK WIZARD (tried and reverted in 0.7.259)
// Recolouring the wizard's controls from [Code] does NOT work. Setting .Color and
// .Font.Color darkens the form, panels and edit fields, but TNewCheckListBox (the
// Tasks/Components page) draws its own item text in a hardcoded dark colour, so
// the entries stayed black on a black box — unreadable. Native buttons and the
// progress bar are drawn by the Windows theme engine and ignore .Color too. The
// result was half-themed and worse than the stock wizard.
// The only clean route is Inno's own WizardStyleFile / WizardStyleFileDynamicDark,
// which need a .vsf VCL style file that Inno does not ship. Don't retry the
// [Code] approach.

// Check Windows version. Minimum supported: Windows 10.
// A friendly welcome notice is shown in interactive mode so users who hit the
// SmartScreen prompt know how to proceed. Silent auto-updates skip this.
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
        'Systema is not code-signed, so Windows SmartScreen may show a' + #10 +
        '"Windows protected your PC" prompt. This is expected for any' + #10 +
        'unsigned app. To continue:' + #10 +
        '  1. Click More info' + #10 +
        '  2. Click Run anyway' + #10#10 +
        'You only have to do this once per download.' + #10#10 +
        'Always download from the official GitHub releases page:' + #10 +
        'https://github.com/Coolbright27/Systema/releases' + #10#10 +
        'Click OK to continue.';
      MsgBox(WelcomeMsg, mbInformation, MB_OK);
    end;
    Result := True;
  end;
end;
