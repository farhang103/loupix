; The Windows installer: Welcome, Next, Install, Finish.
; Built by .github/workflows/build.yml with Inno Setup:
;   ISCC /DAppVersion=1.2.3 /DArch=x64 /DPublishDir=C:\path\to\out\win-x64 /DOutDir=C:\path\to\out installer\Loupix.iss
; /DSmoke builds a throwaway copy for testing on a real PC: separate identity, no shortcuts, and it won't touch an installed Loupix.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef PublishDir
  #error Pass /DPublishDir=<folder holding the published Loupix.exe>
#endif
#ifndef OutDir
  #define OutDir "."
#endif

[Setup]
#ifdef Smoke
AppId={{5B1E0C52-3F0B-4D7E-9B2A-SM0KE0000000}
#else
AppId={{8E2F5C4A-6B1D-4F3E-9A77-3C1D2E5B7A10}
#endif
AppName=Loupix
AppVersion={#AppVersion}
AppVerName=Loupix {#AppVersion}
AppPublisher=Farhang Alizadeh
AppPublisherURL=https://farhang103.github.io/loupix/
AppSupportURL=https://github.com/farhang103/loupix/issues
VersionInfoVersion={#AppVersion}

; Per-user install: no administrator prompt, and upgrades a copy already in this folder.
PrivilegesRequired=lowest
DefaultDirName={autopf}\Loupix
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
DisableReadyPage=yes

#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

; A running Loupix holds its own file open, so close it for the upgrade.
CloseApplications=force
RestartApplications=no

WizardStyle=modern
WizardImageFile=wizard.bmp
WizardSmallImageFile=wizard-small.bmp
SetupIconFile=..\src\Loupix\Assets\loupix.ico
UninstallDisplayIcon={app}\Loupix.exe
UninstallDisplayName=Loupix

Compression=lzma2
SolidCompression=yes
OutputDir={#OutDir}
#ifdef Smoke
OutputBaseFilename=Loupix-Setup-{#Arch}-smoke
#else
OutputBaseFilename=Loupix-Setup-{#Arch}
#endif

[Messages]
WelcomeLabel2=Loupix is a fast, free screenshot tool for Windows.%n%nClick Next to install it. It only takes a moment.

[Tasks]
Name: desktopicon; Description: "Add a shortcut to my desktop"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs

#ifndef Smoke
[Icons]
Name: "{autoprograms}\Loupix"; Filename: "{app}\Loupix.exe"
Name: "{autodesktop}\Loupix"; Filename: "{app}\Loupix.exe"; Tasks: desktopicon

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM Loupix.exe"; Flags: runhidden; RunOnceId: "StopLoupix"
#endif

[Run]
Filename: "{app}\Loupix.exe"; Description: "Open Loupix now"; Flags: nowait postinstall skipifsilent
; Loupix's own updater asks for this. Other silent installs (like the check in CI) must not leave Loupix running.
Filename: "{app}\Loupix.exe"; Parameters: "--updated"; Flags: nowait; Check: RelaunchRequested

[Code]
function RelaunchRequested: Boolean;
begin
  Result := ExpandConstant('{param:relaunch|0}') = '1';
end;

#ifndef Smoke
[Code]
{ Loupix registers these for the current user when it runs (SystemIntegration.cs), so remove them with it. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Extensions: TArrayOfString;
  I: Integer;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Loupix');
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\loupix');
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\Applications\Loupix.exe');
  Extensions := ['.png', '.jpg', '.jpeg', '.bmp', '.gif', '.webp', '.tif', '.tiff'];
  for I := 0 to GetArrayLength(Extensions) - 1 do
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\' + Extensions[I] + '\OpenWithList\Loupix.exe');
end;
#endif
