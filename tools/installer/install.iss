; 工单邮件管理器 安装脚本（Inno Setup 6/7）
#define MyAppName "工单邮件管理器"
#define MyAppVersion "1.0.1"
#define MyAppPublisher "TicketManager"
#define MyAppExeName "TicketManager.exe"
#define MyAppId "4F6C0E1A-2B3C-4D5E-8F90-123456789ABC"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\TicketManager
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\..\installer
OutputBaseFilename=TicketManager-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=110
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
UninstallDisplayIcon={app}\{#MyAppExeName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
SetupIconFile=..\..\src\TicketManager\TicketManager.ico
UninstallDisplayName={#MyAppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
Source: "..\..\src\TicketManager\bin\Release\net8.0-windows\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// ---- 检测 .NET 8 Desktop Runtime（framework-dependent 安装包需要）----

// 版本字符串比较：v1>v2 返回 1，相等 0，v1<v2 返回 -1
function CompareVersions(v1, v2: String): Integer;
var
  p1, p2, n1, n2: Integer;
  part1, part2: String;
begin
  Result := 0;
  while (v1 <> '') or (v2 <> '') do
  begin
    p1 := Pos('.', v1);
    p2 := Pos('.', v2);
    if p1 = 0 then p1 := Length(v1) + 1;
    if p2 = 0 then p2 := Length(v2) + 1;
    part1 := Copy(v1, 1, p1 - 1);
    part2 := Copy(v2, 1, p2 - 1);
    n1 := StrToIntDef(part1, 0);
    n2 := StrToIntDef(part2, 0);
    if n1 < n2 then begin Result := -1; Exit; end;
    if n1 > n2 then begin Result := 1; Exit; end;
    v1 := Copy(v1, p1 + 1, Length(v1));
    v2 := Copy(v2, p2 + 1, Length(v2));
  end;
end;

// 目录下是否存在以 prefix 开头的版本子目录（如 "8." 匹配 8.0.x）
function HasVersionDir(base, prefix: String): Boolean;
var
  findRec: TFindRec;
begin
  Result := False;
  if FindFirst(base + '\*', findRec) then
  try
    repeat
      // findRec.Attributes 位 4（16）= 目录
      if (findRec.Attributes and 16 <> 0) and (Copy(findRec.Name, 1, Length(prefix)) = prefix) then
      begin
        Result := True;
        Exit;
      end;
    until not FindNext(findRec);
  finally
    FindClose(findRec);
  end;
end;

// 检查 .NET 8 Desktop Runtime：注册表（官方安装）或常见文件系统位置（系统/用户/便携）
function IsDotNet8DesktopInstalled(): Boolean;
var
  ver, profile, base: String;
begin
  Result := False;
  // 1) 注册表：官方安装包会写入
  if RegQueryStringValue(HKLM64,
      'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App',
      'Version', ver) then
    Result := (CompareVersions(ver, '8.0.0') >= 0) and (CompareVersions(ver, '9.0.0') < 0);
  // 2) 系统目录 Program Files\dotnet\shared
  if not Result then
  begin
    base := ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App');
    if DirExists(base) then
      Result := HasVersionDir(base, '8.');
  end;
  // 3) 用户级安装 %LOCALAPPDATA%\Microsoft\dotnet\shared
  if not Result then
  begin
    base := ExpandConstant('{localappdata}\Microsoft\dotnet\shared\Microsoft.WindowsDesktop.App');
    if DirExists(base) then
      Result := HasVersionDir(base, '8.');
  end;
  // 4) 用户主目录 %USERPROFILE%\.dotnet\shared（如 VS Code 本地 runtime）
  if not Result then
  begin
    profile := GetEnv('USERPROFILE');
    if profile <> '' then
    begin
      base := AddBackslash(profile) + '.dotnet\shared\Microsoft.WindowsDesktop.App';
      if DirExists(base) then
        Result := HasVersionDir(base, '8.');
    end;
  end;
end;

// 安装前检查：缺失则提示并引导下载，阻止继续安装
function InitializeSetup(): Boolean;
var
  res: Integer;
begin
  Result := True;
  if not IsDotNet8DesktopInstalled() then
  begin
    res := MsgBox('本程序需要 .NET 8 Desktop Runtime（约 20MB）才能运行。'#13#10 +
      '检测到你的电脑尚未安装。'#13#10#13#10 +
      '是否立即打开微软官方下载页面？'#13#10 +
      '安装完成后请重新运行本安装程序。',
      mbInformation, MB_YESNO);
    if res = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0',
        '', '', SW_SHOWNORMAL, ewNoWait, res);
    Result := False;
  end;
end;
