; NekoNexus for UberStrike - one-click installer (Inno Setup).
; Produces a single NekoNexusSetup.exe that auto-detects UberStrike from Steam and patches it
; to play on the NekoNexus free server. No .cmd, no PowerShell - pure native installer.
;
; Build:  iscc installer\nekonexus-setup.iss   (after misc\bundle-client.ps1 has produced the
;         self-contained toolkit in .release\client\_pak\free-server\).

#define AppName    "NekoNexus for UberStrike"
#define AppVer     "4.7.14"
#define Publisher  "NekoSuneVR"
#define ServerHost "nekonexus.nekosunevr.co.uk"
; payload produced by misc\bundle-client.ps1
#define Payload    "..\.release\client\_pak\free-server"

[Setup]
AppId={{8F3A1C42-9B7E-4D6A-B1E2-7C5D9A0F4E81}
AppName={#AppName}
AppVersion={#AppVer}
AppPublisher={#Publisher}
WizardStyle=modern
DisableDirPage=yes
DisableProgramGroupPage=yes
CreateAppDir=no
Uninstallable=no
PrivilegesRequired=admin
OutputDir=..\.release\installer
OutputBaseFilename=NekoNexusSetup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
; ---- when you have a code-signing cert, uncomment to sign the produced exe ----
; SignTool=signtool
; SignedUninstaller=yes

[Messages]
WelcomeLabel2=This will patch your UberStrike to play on the NekoNexus free server (%n%n{#ServerHost}).%n%nPlease CLOSE UberStrike (and quit it in Steam) before continuing.

[Files]
; The whole self-contained toolkit is extracted to a temp folder during install.
Source: "{#Payload}\patcher\*";                          DestDir: "{tmp}\p\patcher"; Flags: recursesubdirs ignoreversion deleteafterinstall
Source: "{#Payload}\mod\*";                              DestDir: "{tmp}\p\mod";     Flags: ignoreversion deleteafterinstall
Source: "{#Payload}\plugins\*";                          DestDir: "{tmp}\p\plugins"; Flags: ignoreversion deleteafterinstall skipifsourcedoesntexist
Source: "{#Payload}\Photon3Unity3D.dll";                 DestDir: "{tmp}\p";         Flags: ignoreversion deleteafterinstall
Source: "{#Payload}\NekoNexus.Patch.xml";                 DestDir: "{tmp}\p";         Flags: ignoreversion deleteafterinstall
Source: "{#Payload}\NekoNexus.Settings.Client.template.xml"; DestDir: "{tmp}\p";      Flags: ignoreversion deleteafterinstall

[Code]
var
  GamePage: TInputDirWizardPage;
  DetectedGame: string;
  DownloadPage: TDownloadWizardPage;

{ ---- locate UberStrike via Steam (registry -> libraryfolders.vdf -> fallbacks) ---- }
function NormSlashes(S: string): string;
begin
  StringChangeEx(S, '/', '\', True);
  Result := S;
end;

function HasGame(Folder: string): Boolean;
begin
  Result := (Folder <> '') and
    FileExists(AddBackslash(Folder) + 'UberStrike_Data\Managed\Assembly-CSharp.dll');
end;

procedure CollectVdfPaths(Vdf: string; Libs: TStringList);
var
  Content: AnsiString;
  Val: string;
  P, Q, R: Integer;
begin
  if not LoadStringFromFile(Vdf, Content) then Exit;
  P := 1;
  repeat
    Q := Pos('"path"', Content);
    if Q = 0 then Break;
    Content := Copy(Content, Q + 6, Length(Content));   { advance past this "path" }
    R := Pos('"', Content);                              { opening quote of value }
    if R = 0 then Break;
    Content := Copy(Content, R + 1, Length(Content));
    R := Pos('"', Content);                              { closing quote }
    if R = 0 then Break;
    Val := Copy(Content, 1, R - 1);
    StringChangeEx(Val, '\\', '\', True);
    Libs.Add(NormSlashes(Val));
    Content := Copy(Content, R + 1, Length(Content));
  until False;
end;

function FindUberStrike(): string;
var
  Steam, V: string;
  Libs: TStringList;
  I: Integer;
  G: string;
begin
  Result := '';
  Libs := TStringList.Create;
  try
    Steam := '';
    if RegQueryStringValue(HKCU, 'Software\Valve\Steam', 'SteamPath', V) then Steam := V;
    if (Steam = '') and RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Valve\Steam', 'InstallPath', V) then Steam := V;
    if (Steam = '') and RegQueryStringValue(HKLM, 'SOFTWARE\Valve\Steam', 'InstallPath', V) then Steam := V;
    if Steam <> '' then
    begin
      Steam := NormSlashes(Steam);
      Libs.Add(Steam);
      CollectVdfPaths(AddBackslash(Steam) + 'steamapps\libraryfolders.vdf', Libs);
    end;
    Libs.Add(ExpandConstant('{commonpf32}\Steam'));
    Libs.Add(ExpandConstant('{commonpf}\Steam'));
    for I := 0 to Libs.Count - 1 do
    begin
      G := AddBackslash(Libs[I]) + 'steamapps\common\UberStrike';
      if HasGame(G) then
      begin
        Result := G;
        Exit;
      end;
    end;
  finally
    Libs.Free;
  end;
end;

{ The patcher (UniversalUnityPatcher) needs .NET Framework 4.7.2+. Win10/11 ship 4.8;
  only older/stripped Windows lacks it. Check the standard NDP\v4\Full Release value. }
function NetFx472Present(): Boolean;
var
  Rel: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Rel) and (Rel >= 461808);
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;   { keep going (Cancel handled by the page) }
end;

{ Download + silently install .NET Framework 4.8 (covers the 4.7.2 requirement). Returns True
  on success. Only called when .NET is missing. }
function InstallDotNet(): Boolean;
var
  RC: Integer;
begin
  Result := False;
  DownloadPage.Clear;
  DownloadPage.Add('https://go.microsoft.com/fwlink/?linkid=2088631', 'ndp48.exe', '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      MsgBox('Could not download .NET Framework:' + #13#10 + GetExceptionMessage + #13#10 + #13#10 +
             'Install it manually from https://dotnet.microsoft.com/download/dotnet-framework, then run this again.',
             mbError, MB_OK);
      Exit;
    end;
  finally
    DownloadPage.Hide;
  end;
  { /passive shows Microsoft's own progress with no prompts; /norestart so we control reboot. }
  if not Exec(ExpandConstant('{tmp}\ndp48.exe'), '/passive /norestart', '', SW_SHOWNORMAL, ewWaitUntilTerminated, RC) then
  begin
    MsgBox('Could not start the .NET Framework installer.', mbError, MB_OK);
    Exit;
  end;
  { 0 = ok, 3010 = ok but reboot needed; otherwise re-check the registry to be sure. }
  Result := (RC = 0) or (RC = 3010) or NetFx472Present();
  if not Result then
    MsgBox('.NET Framework install did not complete (code ' + IntToStr(RC) + ').' + #13#10 +
           'Install it manually and run this installer again.', mbError, MB_OK);
end;

procedure InitializeWizard();
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), @OnDownloadProgress);
  DetectedGame := FindUberStrike();
  GamePage := CreateInputDirPage(wpWelcome,
    'UberStrike location',
    'Confirm where UberStrike is installed.',
    'Setup found your game below (or pick the folder that contains UberStrike_Data). Click Next to patch it.',
    False, '');
  GamePage.Add('UberStrike folder');
  if DetectedGame <> '' then
    GamePage.Values[0] := DetectedGame
  else
    GamePage.Values[0] := ExpandConstant('{commonpf32}\Steam\steamapps\common\UberStrike');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = GamePage.ID then
  begin
    if not HasGame(GamePage.Values[0]) then
    begin
      MsgBox('That folder does not look like an UberStrike install' + #13#10 +
             '(missing UberStrike_Data\Managed\Assembly-CSharp.dll).' + #13#10 +
             'Pick your UberStrike folder and try again.', mbError, MB_OK);
      Result := False;
    end;
  end
  else if CurPageID = wpReady then
  begin
    { The patcher needs .NET Framework 4.7.2+. If it's missing, download + install it now. }
    if not NetFx472Present() then
      Result := InstallDotNet();
  end;
end;

{ ---- the patch itself ---- }
function CopyOrFail(Src, Dst: string): Boolean;
begin
  Result := FileCopy(Src, Dst, False);
end;

{ Delete every file matching Mask in Dir (non-recursive, files only). Used to nuke leftovers
  from a previous Paradise install so NekoNexus is a clean replacement. Safe: UberStrike's own
  files are never named "Paradise*", and the "LostParadise" map starts with "Lost". }
procedure DeleteByMask(Dir, Mask: string);
var
  FR: TFindRec;
  Base: string;
begin
  if (Dir = '') or not DirExists(Dir) then Exit;
  Base := AddBackslash(Dir);
  if FindFirst(Base + Mask, FR) then
  begin
    try
      repeat
        if (FR.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0 then
          DeleteFile(Base + FR.Name);
      until not FindNext(FR);
    finally
      FindClose(FR);
    end;
  end;
end;

{ Remove all artifacts of a prior Paradise install from the game folder. }
procedure CleanupOldParadise(Managed, DataDir: string);
begin
  { old mod assemblies + logs in Managed }
  DeleteByMask(Managed, 'Paradise*.dll');
  DeleteByMask(Managed, 'Paradise*.log');
  DeleteByMask(Managed, 'Paradise*.xml');
  { old client settings + logs in UberStrike_Data }
  DeleteByMask(DataDir, 'Paradise*.xml');
  DeleteByMask(DataDir, 'Paradise*.log');
  { old Discord Rich Presence helper in UberStrike_Data\Plugins }
  DeleteByMask(AddBackslash(DataDir) + 'Plugins', 'Paradise*.exe');
  DeleteByMask(AddBackslash(DataDir) + 'Plugins', 'Paradise*.dll');
end;

function DoPatch(): Boolean;
var
  Game, Managed, DataDir, Tmp, Mgd, PatchXml, Shim, BackupDll, GamePhoton, Tpl, LogFile, Plugins: string;
  TplA, LogA: AnsiString;
  RC: Integer;
  Mods: TArrayOfString;
  I: Integer;
begin
  Result := False;
  Game    := RemoveBackslashUnlessRoot(GamePage.Values[0]);
  Managed := AddBackslash(Game) + 'UberStrike_Data\Managed';
  DataDir := AddBackslash(Game) + 'UberStrike_Data';
  Tmp     := ExpandConstant('{tmp}\p');
  PatchXml := Tmp + '\NekoNexus.Patch.xml';
  Shim     := Tmp + '\Photon3Unity3D.dll';

  Mgd := AddBackslash(Managed);

  { 1) restore a clean Assembly-CSharp from backup (so re-running is safe) }
  BackupDll := Mgd + 'backup\Assembly-CSharp.dll';
  if FileExists(BackupDll) then
    FileCopy(BackupDll, Mgd + 'Assembly-CSharp.dll', False);

  { 1b) nuke any leftovers from a previous Paradise install so NekoNexus replaces it cleanly.
        (Done after the backup-restore above, which already wipes Paradise's injected patch.) }
  CleanupOldParadise(Managed, DataDir);

  { 2) install the prebuilt mod DLLs FIRST. The patch injects a Call into
       NekoNexus.Client.Bootstrap, so that assembly must already be in Managed for the patcher to
       resolve it - otherwise the patcher fails (exit code 1, stops at "patch method Awake"). }
  SetArrayLength(Mods, 5);
  Mods[0] := 'NekoNexus.Client.Bootstrap.dll';
  Mods[1] := 'NekoNexus.Client.dll';
  Mods[2] := '0Harmony.dll';
  Mods[3] := 'log4net.dll';
  Mods[4] := 'YamlDotNet.dll';
  for I := 0 to GetArrayLength(Mods) - 1 do
  begin
    if FileExists(Tmp + '\mod\' + Mods[I]) then
    begin
      if not CopyOrFail(Tmp + '\mod\' + Mods[I], Mgd + Mods[I]) then
      begin
        MsgBox('Could not copy ' + Mods[I] + ' (is UberStrike still running?).', mbError, MB_OK);
        Exit;
      end;
    end;
  end;

  { 3) replace the Photon transport with our free shim (back up the original once) }
  GamePhoton := Mgd + 'Photon3Unity3D.dll';
  if FileExists(GamePhoton) and not FileExists(GamePhoton + '.orig') then
    FileCopy(GamePhoton, GamePhoton + '.orig', False);
  if not CopyOrFail(Shim, GamePhoton) then
  begin
    MsgBox('Could not install the free transport (is UberStrike still running?).', mbError, MB_OK);
    Exit;
  end;

  { 3b) install optional plugins (Discord Rich Presence helper) into UberStrike_Data\Plugins.
       The mod launches this exe as a child process; without it Discord Rich Presence does nothing. }
  Plugins := AddBackslash(DataDir) + 'Plugins';
  if FileExists(Tmp + '\plugins\NekoNexus.Client.DiscordRPC.exe') then
  begin
    if not DirExists(Plugins) then
      CreateDir(Plugins);
    FileCopy(Tmp + '\plugins\NekoNexus.Client.DiscordRPC.exe', AddBackslash(Plugins) + 'NekoNexus.Client.DiscordRPC.exe', False);
  end;

  { 4) NOW patch Assembly-CSharp (bootstrap present -> the Call resolves), capturing output. }
  LogFile := ExpandConstant('{tmp}\nekonexus-patch.log');
  if not Exec(ExpandConstant('{cmd}'),
       '/C ""' + Tmp + '\patcher\UniversalUnityPatcher.exe" --backup --no-gui --silent --ignore-duplicate-patch -i "' + Managed + '" -p "' + PatchXml + '" > "' + LogFile + '" 2>&1"',
       Tmp + '\patcher', SW_HIDE, ewWaitUntilTerminated, RC) then
  begin
    MsgBox('Could not run the patch step.', mbError, MB_OK);
    Exit;
  end;
  if RC <> 0 then
  begin
    LogA := '';
    LoadStringFromFile(LogFile, LogA);
    MsgBox('Patching failed (exit code ' + IntToStr(RC) + ').' + #13#10 +
           'Game folder: ' + Managed + #13#10 + #13#10 +
           'Patcher output:' + #13#10 + String(LogA) + #13#10 +
           'Make sure UberStrike is fully closed, then run this installer again.',
           mbError, MB_OK);
    Exit;
  end;

  { 5) write the settings file pointing at the NekoNexus server (HTTPS domain) }
  if LoadStringFromFile(Tmp + '\NekoNexus.Settings.Client.template.xml', TplA) then
  begin
    Tpl := TplA;
    StringChangeEx(Tpl, 'http://SERVER_HOST:8080/2.0/',    'https://{#ServerHost}/2.0/', True);
    StringChangeEx(Tpl, 'http://SERVER_HOST:8081/images/', 'https://{#ServerHost}/images/', True);
    StringChangeEx(Tpl, 'http://SERVER_HOST:8081/updates/','https://{#ServerHost}/updates/', True);
    SaveStringToFile(AddBackslash(DataDir) + 'NekoNexus.Settings.Client.xml', AnsiString(Tpl), False);
  end;

  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if DoPatch() then
      MsgBox('Done! UberStrike is patched for the NekoNexus server (' + '{#ServerHost}' + ').' + #13#10 +
             'Launch the game from Steam to play.', mbInformation, MB_OK);
  end;
end;
