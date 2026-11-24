; Instalador do Orbi (Inno Setup). Gerado pelo GitHub Actions: .github/workflows/build.yml
#define Version GetEnv("ORBI_VERSION")

[Setup]
AppId={{6F3B2C1A-9D4E-4B7A-8E21-3C5D7F9A0B12}
AppName=Orbi
AppVersion={#Version}
AppPublisher=Halfszin
DefaultDirName={localappdata}\Programs\Orbi
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=dist
OutputBaseFilename=OrbiSetup
SetupIconFile=orbi.ico
UninstallDisplayIcon={app}\Orbi.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "pt"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "publish\Orbi.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Orbi"; Filename: "{app}\Orbi.exe"
Name: "{autodesktop}\Orbi"; Filename: "{app}\Orbi.exe"; Tasks: desktopicon

[Registry]
; Remove o "Iniciar com o Windows" ao desinstalar.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Orbi"; Flags: uninsdeletevalue

[Run]
; Sem "skipifsilent": na atualização automática (instalação silenciosa) o Orbi abre de novo sozinho.
Filename: "{app}\Orbi.exe"; Description: "Abrir o Orbi"; Flags: nowait postinstall

[UninstallRun]
Filename: "taskkill"; Parameters: "/im Orbi.exe /f"; Flags: runhidden; RunOnceId: "FecharOrbi"
