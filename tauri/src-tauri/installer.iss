; Instalador do Orbi em Rust (Inno Setup). Gerado pelo CI: .github/workflows/tauri.yml
; AppId e nome do .exe próprios: instala ao lado do Orbi em C# sem um mexer no outro.
#define Version GetEnv("ORBI_VERSION")

[Setup]
AppId={{B1E7D3A2-5C84-4F6E-9A1D-7E2F4C8B9D30}
AppName=Orbi (Rust)
AppVersion={#Version}
AppPublisher=Halfszin
; Pasta do usuário: o atualizador troca o .exe sem pedir administrador.
DefaultDirName={localappdata}\Programs\Orbi Rust
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir=dist
OutputBaseFilename=OrbiRustSetup
SetupIconFile=..\..\orbi.ico
UninstallDisplayIcon={app}\OrbiRust.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "pt"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "target\release\orbi.exe"; DestDir: "{app}"; DestName: "OrbiRust.exe"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Orbi (Rust)"; Filename: "{app}\OrbiRust.exe"
Name: "{autodesktop}\Orbi (Rust)"; Filename: "{app}\OrbiRust.exe"; Tasks: desktopicon

[Registry]
; Remove o "Iniciar com o Windows" ao desinstalar.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "OrbiRust"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\OrbiRust.exe"; Description: "Abrir o Orbi"; Flags: nowait postinstall

[UninstallRun]
Filename: "taskkill"; Parameters: "/im OrbiRust.exe /f"; Flags: runhidden; RunOnceId: "FecharOrbiRust"

[UninstallDelete]
; Sobras da atualização automática (o .exe velho e downloads pela metade).
Type: files; Name: "{app}\OrbiRust.exe.old"
Type: files; Name: "{app}\OrbiRust.exe.part"
