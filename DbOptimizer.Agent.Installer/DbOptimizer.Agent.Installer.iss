; SqlBrain Agent Installer
; Inno Setup 6.x
; Company: ThoMetrics AI LLC
; Product: SqlBrain Agent (internal: DbOptimizer.Agent)
;
; Build instructions:
;   1. dotnet publish DbOptimizer.Agent/DbOptimizer.Agent.csproj ^
;        --configuration Release --runtime win-x64 --self-contained true ^
;        --output ./publish/win-x64
;   2. Open this .iss in Inno Setup Compiler and click Build > Compile
;   3. Output: Output/SqlBrainAgentSetup.exe
;   4. Upload SqlBrainAgentSetup.exe to S3 and update dashboard download link

#define MyAppName      "SqlBrain Agent"
#define MyAppVersion   "1.0.0"
#define MyAppPublisher "ThoMetrics AI LLC"
#define MyAppURL       "https://sqlbrain.ai"
#define MyServiceName  "SqlBrainAgent"
#define MyServiceExe   "DbOptimizer.Agent.exe"
#define MyDefaultURL   "https://api.sqlbrain.ai"
#define MySourceDir    "..\publish\win-x64"

[Setup]
AppId={{A3F2E1D4-7B6C-4F8A-9E2D-1C3B5A7F9E0D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={commonpf}\ThoMetrics AI\SqlBrain Agent
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=SqlBrainAgentSetup
SetupIconFile=
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyServiceExe}
AppMutex=SqlBrainAgentSetupMutex

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Run]
Filename: "sc.exe"; Parameters: "create {#MyServiceName} binPath= ""{app}\{#MyServiceExe}"" start= auto DisplayName= ""SqlBrain Agent"""; Flags: runhidden waituntilterminated; StatusMsg: "Registering SqlBrain Agent Windows Service..."
Filename: "sc.exe"; Parameters: "description {#MyServiceName} ""SqlBrain Agent - automated SQL Server optimization by ThoMetrics AI"""; Flags: runhidden waituntilterminated
Filename: "sc.exe"; Parameters: "start {#MyServiceName}"; Flags: runhidden waituntilterminated; StatusMsg: "Starting SqlBrain Agent..."

[UninstallRun]
Filename: "sc.exe"; Parameters: "stop {#MyServiceName}"; Flags: runhidden waituntilterminated
Filename: "sc.exe"; Parameters: "delete {#MyServiceName}"; Flags: runhidden waituntilterminated

[Code]

var
  BackendUrlPage:       TInputQueryWizardPage;
  ApiKeyPage:           TInputQueryWizardPage;
  ConnectionStringPage: TInputQueryWizardPage;

function GetBackendUrl(Param: String): String;
begin
  Result := BackendUrlPage.Values[0];
end;

function GetApiKey(Param: String): String;
begin
  Result := ApiKeyPage.Values[0];
end;

function GetConnectionString(Param: String): String;
begin
  Result := ConnectionStringPage.Values[0];
end;

procedure InitializeWizard;
begin
  BackendUrlPage := CreateInputQueryPage(
    wpWelcome,
    'Backend URL',
    'Enter the SqlBrain backend API URL.',
    'Leave as default unless you have been given a custom URL.');
  BackendUrlPage.Add('Backend URL:', False);
  BackendUrlPage.Values[0] := '{#MyDefaultURL}';

  ApiKeyPage := CreateInputQueryPage(
    BackendUrlPage.ID,
    'Agent API Key',
    'Enter your Agent API Key.',
    'Generate this key in the SqlBrain dashboard under Settings > Agents.');
  ApiKeyPage.Add('API Key:', False);
  ApiKeyPage.Values[0] := '';

  ConnectionStringPage := CreateInputQueryPage(
    ApiKeyPage.ID,
    'SQL Server Connection String',
    'Enter the connection string for the SQL Server instance to optimize.',
    'Windows Auth:  Server=MYSERVER;Database=master;Integrated Security=true;TrustServerCertificate=true' + #13#10 +
    'SQL Auth:      Server=MYSERVER;Database=master;User Id=sa;Password=yourpw;TrustServerCertificate=true');
  ConnectionStringPage.Add('Connection String:', False);
  ConnectionStringPage.Values[0] := '';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;

  if CurPageID = BackendUrlPage.ID then
    if Trim(BackendUrlPage.Values[0]) = '' then
    begin
      MsgBox('Backend URL is required.', mbError, MB_OK);
      Result := False;
    end;

  if CurPageID = ApiKeyPage.ID then
    if Trim(ApiKeyPage.Values[0]) = '' then
    begin
      MsgBox('API Key is required.', mbError, MB_OK);
      Result := False;
    end;

  if CurPageID = ConnectionStringPage.ID then
    if Trim(ConnectionStringPage.Values[0]) = '' then
    begin
      MsgBox('Connection String is required.', mbError, MB_OK);
      Result := False;
    end;
end;

procedure WriteConfig();
var
  ConfigPath: String;
  Lines:      TStringList;
  i:          Integer;
  Line:       String;
begin
  ConfigPath := ExpandConstant('{app}\appsettings.json');
  Lines := TStringList.Create;
  Lines.LoadFromFile(ConfigPath);
  for i := 0 to Lines.Count - 1 do
  begin
    Line := Lines[i];
    StringChangeEx(Line, 'INSTALLER_PLACEHOLDER_BACKENDURL',       GetBackendUrl(''),       True);
    StringChangeEx(Line, 'INSTALLER_PLACEHOLDER_APIKEY',           GetApiKey(''),           True);
    StringChangeEx(Line, 'INSTALLER_PLACEHOLDER_CONNECTIONSTRING', GetConnectionString(''), True);
    Lines[i] := Line;
  end;
  Lines.SaveToFile(ConfigPath);
  Lines.Free;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    WriteConfig();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec('sc.exe', 'stop {#MyServiceName}',   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('sc.exe', 'delete {#MyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
