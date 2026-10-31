; Compiled by tools/package-exe.ps1 using Inno Setup 6.3 or later.
#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef AppArchitecture
  #define AppArchitecture "x64"
#endif

[Setup]
AppId={{1C182CE1-14C9-4CA4-A106-B138718A40F2}
AppName=TransFlux
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher=TransFlux
DefaultDirName={localappdata}\Programs\TransFlux
DefaultGroupName=TransFlux
DisableProgramGroupPage=yes
DisableDirPage=yes
DirExistsWarning=no
UsePreviousAppDir=yes
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=yes
UsePreviousLanguage=yes
PrivilegesRequired=lowest
MinVersion=10.0.22621
#if AppArchitecture == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
OutputDir={#OutputDir}
OutputBaseFilename=TransFlux-Setup-{#AppVersion}-{#AppArchitecture}
SetupIconFile={#PayloadDir}\Assets\AppIcon.ico
UninstallDisplayIcon={app}\TransFlux.exe
LicenseFile={#PayloadDir}\LICENSE.txt
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Languages\LICENSE.txt"; DestDir: "{app}\Licenses"; DestName: "InnoSetup-ChineseSimplified.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\TransFlux"; Filename: "{app}\TransFlux.exe"
Name: "{autodesktop}\TransFlux"; Filename: "{app}\TransFlux.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\TransFlux.exe"; Description: "{cm:LaunchProgram,TransFlux}"; Flags: nowait postinstall skipifsilent