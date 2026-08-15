; Instalador do Deadheim Launcher (Inno Setup).
;
; Como gerar (use installer\build.ps1, que faz os dois passos):
;   1) dotnet publish DeadheimLauncher\DeadheimLauncher.csproj -c Release -r win-x64 ^
;        --self-contained true -o publish\DeadheimLauncher
;   2) iscc installer\DeadheimLauncher.iss
;   -> installer\Output\DeadheimLauncherSetup.exe
;
; ------------------------------------------------------------------------------
; SOBRE OS AVISOS DE SEGURANÇA DO WINDOWS
;
; Existem DOIS avisos diferentes, e eles têm soluções diferentes:
;
; 1) UAC ("deseja permitir que este app faça alterações no dispositivo?")
;    RESOLVIDO aqui. PrivilegesRequired=lowest + instalação em {localappdata}
;    significa que nada é escrito em Program Files, então o Windows não precisa
;    elevar nada. O app também declara asInvoker no app.manifest.
;
; 2) SmartScreen ("o Windows protegeu o seu PC" / "Executar assim mesmo")
;    NÃO some só com configuração — ele aparece porque o .exe não tem assinatura
;    digital com reputação. As únicas saídas reais são:
;      a) Assinar com um certificado de code signing. O mais barato hoje é o
;         Microsoft Trusted Signing (~US$10/mês). Com ele, descomente a linha
;         SignTool abaixo e registre a ferramenta no Inno Setup.
;      b) Deixar a reputação acumular: conforme o mesmo binário (byte a byte)
;         for baixado e executado por mais gente sem incidente, o SmartScreen
;         para de avisar. Sem assinatura isso leva centenas de downloads e
;         reinicia a cada nova versão.
;    O que dá pra fazer sem certificado, e já está feito aqui: publicar como
;    pasta self-contained em vez de single-file (o auto-extraível é o padrão que
;    mais dispara heurística de antivírus), usar um instalador comum e assinável,
;    e preencher os metadados do binário (ver o .csproj).
; ------------------------------------------------------------------------------

#define MyAppName "Deadheim Launcher"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Deadheim"
#define MyAppExeName "DeadheimLauncher.exe"

[Setup]
AppId={{7B7B0B6E-6E7B-4B9E-9C7B-1E7B6E1D9A10}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup

; Instala no perfil do usuário: sem isso o Windows exigiria elevação (UAC).
DefaultDirName={localappdata}\DeadheimLauncher
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=DeadheimLauncherSetup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}

; Com certificado de code signing, descomente a linha abaixo (e configure a
; ferramenta "signtool" em Tools > Configure Sign Tools do Inno Setup).
; Isso é o que remove o aviso do SmartScreen.
;SignTool=signtool

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\publish\DeadheimLauncher\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Só o que o instalador colocou. Perfis e mods do jogador ficam em
; %AppData%\DeadheimLauncher e são preservados de propósito ao desinstalar.
Type: filesandordirs; Name: "{app}"
