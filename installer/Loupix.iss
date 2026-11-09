; The Windows installer: one Install button, then Loupix opens by itself. Updates ask nothing.
; Built by .github/workflows/build.yml with Inno Setup:
;   ISCC /DAppVersion=1.2.3 /DArch=x64 /DPublishDir=C:\path\to\out\win-x64 /DOutDir=C:\path\to\out installer\Loupix.iss
; /DSmoke builds a throwaway copy for testing on a real PC: its own identity, folder and settings, no shortcuts,
; and it leaves an installed Loupix alone. Add /DAppExe=SomethingElse.exe to test with a stand-in program, because
; a real Loupix.exe would hand over to a Loupix that's already running.

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
#ifndef AppExe
  #define AppExe "Loupix.exe"
#endif

#ifdef Smoke
  #define InstallFolder "Loupix-Smoke"
  #define SettingsFolder "Loupix-Smoke"
#else
  #define InstallFolder "Loupix"
  #define SettingsFolder "Loupix"
#endif

[Setup]
#ifdef Smoke
AppId={{5B1E0C52-3F0B-4D7E-9B2A-5A0CE0000000}
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
DefaultDirName={autopf}\{#InstallFolder}
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
DisableReadyPage=yes
DisableFinishedPage=yes

; The oldest Windows Loupix runs on (Directory.Build.props, SupportedOSPlatformVersion).
MinVersion=10.0.17763

#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

; PrepareToInstall closes a running Loupix itself, so an update doesn't stop to ask.
CloseApplications=no

WizardStyle=modern dynamic windows11
WizardImageFile=wizard.bmp
WizardSmallImageFile=wizard-small.bmp
; Without these, dark mode swaps the logo for Inno's stock picture.
WizardImageFileDynamicDark=wizard.bmp
WizardSmallImageFileDynamicDark=wizard-small-dark.bmp
SetupIconFile=..\src\Loupix\Assets\loupix.ico
UninstallDisplayIcon={app}\{#AppExe}
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
SetupWindowTitle=Install Loupix
WelcomeLabel1=Install Loupix
WelcomeLabel2=A fast, free screenshot tool for Windows.%n%nInstalls just for you, with no administrator prompt.%n%nTakes a few seconds. You can remove it any time from Windows Settings.
ClickNext=
WizardInstalling=Installing Loupix
InstallingLabel=This takes a few seconds.
StatusExtractFiles=Copying files...

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs

#ifndef Smoke
[Icons]
Name: "{autoprograms}\Loupix"; Filename: "{app}\{#AppExe}"
#endif

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "StopLoupix"

[Run]
Filename: "{app}\{#AppExe}"; Flags: nowait skipifsilent; Check: not IsUpdate
Filename: "{app}\{#AppExe}"; Parameters: "--background"; Flags: nowait; Check: WasRunning and not RelaunchRequested
; Loupix's own updater asks for this. Other silent installs (like the check in CI) must not leave Loupix running.
Filename: "{app}\{#AppExe}"; Parameters: "--updated"; Flags: nowait; Check: RelaunchRequested

[Code]
const
  BM_CLICK = $00F5;

var
  Updating, LoupixWasRunning: Boolean;

function RelaunchRequested: Boolean;
begin
  Result := ExpandConstant('{param:relaunch|0}') = '1';
end;

function IsUpdate: Boolean;
begin
  Result := Updating;
end;

function WasRunning: Boolean;
begin
  Result := LoupixWasRunning;
end;

procedure InitializeWizard;
begin
  Updating := WizardForm.PrevAppDir <> '';
  if Updating then
    WizardForm.Caption := 'Update Loupix';
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := Updating and (PageID = wpWelcome);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpWelcome then
    WizardForm.NextButton.Caption := SetupMessage(msgButtonInstall);
  { Inno always shows at least one page, so an update presses Install on the Ready page for you.
    Posted, because the wizard isn't on screen yet when this first runs. }
  if Updating and (CurPageID = wpReady) and not WizardSilent then
    PostMessage(WizardForm.NextButton.Handle, BM_CLICK, 0, 0);
  if Updating and (CurPageID = wpInstalling) then
  begin
    WizardForm.PageNameLabel.Caption := 'Updating Loupix';
    WizardForm.PageDescriptionLabel.Caption := 'Your settings stay as they are.';
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
  if Updating and Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) and (ExitCode = 0) then
  begin
    LoupixWasRunning := True;
    Sleep(400); { let Windows release the file before it's replaced }
  end;
end;

function UninstallKey: String;
begin
  Result := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + ExpandConstant('{#SetupSetting("AppId")}') + '_is1';
end;

{ Windows Settings already asks "uninstall this app?", so Inno's own "are you sure?" and "it's gone" boxes are just extra clicks.
  /ASKSETTINGS keeps the one question that matters. Scripts that run the uninstaller with /VERYSILENT aren't asked anything. }
procedure CurStepChanged(CurStep: TSetupStep);
var
  Command: String;
begin
  if (CurStep = ssPostInstall) and RegQueryStringValue(HKCU, UninstallKey, 'UninstallString', Command) then
    RegWriteStringValue(HKCU, UninstallKey, 'UninstallString', Command + ' /SILENT /ASKSETTINGS');
end;

function ShouldAskAboutSettings: Boolean;
var
  I: Integer;
begin
  Result := not UninstallSilent;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/ASKSETTINGS') = 0 then
      Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
#ifndef Smoke
var
  Extensions: TArrayOfString;
  I: Integer;
#endif
begin
  if CurUninstallStep <> usPostUninstall then Exit;
#ifndef Smoke
  { Loupix registers these for the current user when it runs (SystemIntegration.cs), so remove them with it. }
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Loupix');
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\loupix');
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\Applications\Loupix.exe');
  Extensions := ['.png', '.jpg', '.jpeg', '.bmp', '.gif', '.webp', '.tif', '.tiff'];
  for I := 0 to GetArrayLength(Extensions) - 1 do
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\' + Extensions[I] + '\OpenWithList\Loupix.exe');
#endif
  if ShouldAskAboutSettings then
    if TaskDialogMsgBox('Also remove your Loupix settings?',
      'Loupix itself will be removed either way. Your hotkeys and preferences can stay in case you install it again.',
      mbConfirmation, MB_YESNO, ['Keep settings', 'Remove'], 0) = IDNO then
      DelTree(ExpandConstant('{userappdata}\{#SettingsFolder}'), True, True, True);
end;
