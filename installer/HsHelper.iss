; 爐石輔助 HsHelper 安裝程式
; 流程：檢查電腦是否已安裝官方 Hearthstone Deck Tracker（HDT）
;   - 沒有：從官方發行網址下載 HDT 安裝檔並開啟安裝，接著安裝外掛 DLL
;   - 已有：跳過，只安裝外掛 DLL
; HDT 本身不會被打包進這個安裝程式（HDT 授權不允許轉散布），而是安裝時從官方下載。

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#define HdtInstallerUrl "https://github.com/HearthSim/HDT-Velopack-Releases/releases/latest/download/HDT-Installer.exe"

[Setup]
AppId={{B7E4C2A1-5D3F-4E8B-9A61-2F0C7D9E4A35}
AppName=爐石輔助 HsHelper
AppVersion={#AppVersion}
AppPublisher=rcj811
DefaultDirName={userappdata}\HearthstoneDeckTracker\Plugins\HsHelper
DisableDirPage=yes
DisableProgramGroupPage=yes
UsePreviousAppDir=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=HsHelper-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
WizardStyle=modern
UninstallDisplayName=爐石輔助 HsHelper（HDT 外掛）

[Languages]
Name: "zhtw"; MessagesFile: "ChineseTraditional.isl"

[CustomMessages]
zhtw.HdtPageCaption=Hearthstone Deck Tracker
zhtw.HdtPageDesc=這個外掛需要官方的 Hearthstone Deck Tracker（HDT）才能執行。
zhtw.HdtPageText=這台電腦上沒有偵測到 HDT。安裝程式可以從官方網址下載並開啟 HDT 安裝程式，安裝完成後會接著安裝爐石輔助外掛。
zhtw.HdtOptInstall=下載並安裝官方 HDT（建議）
zhtw.HdtDownloadFailed=下載 HDT 安裝檔失敗：%1%n%n外掛仍會繼續安裝，請之後到 https://hsreplay.net/downloads/ 手動安裝 HDT。
zhtw.HdtRunFailed=無法啟動 HDT 安裝程式（錯誤碼 %1）。外掛仍會繼續安裝，請之後手動安裝 HDT。
zhtw.HdtStillMissing=沒有偵測到 HDT 安裝完成。外掛仍會繼續安裝；若 HDT 安裝被取消，請之後到 https://hsreplay.net/downloads/ 手動安裝。
zhtw.HdtFound=已偵測到官方 HDT，將略過 HDT 安裝，只安裝外掛。
zhtw.DoneHint=安裝完成！%n%n請「重新啟動」Hearthstone Deck Tracker，然後到 選項 → 追蹤器 → 外掛（Options → Tracker → Plugins）確認「爐石輔助 HsHelper」已勾選啟用。%n進入對戰後，遊戲畫面左側就會出現狀態面板。

[Files]
Source: "..\out\HsHelper.dll"; DestDir: "{app}"; Flags: ignoreversion

[Code]
var
  HdtPage: TInputOptionWizardPage;
  DownloadPage: TDownloadWizardPage;
  HdtWasInstalled: Boolean;

function IsHdtInstalled: Boolean;
var
  Root: String;
begin
  Root := ExpandConstant('{localappdata}\HearthstoneDeckTracker');
  Result :=
    FileExists(Root + '\current\HearthstoneDeckTracker.exe') or
    FileExists(Root + '\Hearthstone Deck Tracker.exe') or
    (FileExists(Root + '\Update.exe') and FileExists(Root + '\HearthstoneDeckTracker.exe')) or
    RegKeyExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\HearthstoneDeckTracker');
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

procedure InitializeWizard;
begin
  HdtWasInstalled := IsHdtInstalled;

  HdtPage := CreateInputOptionPage(wpWelcome,
    CustomMessage('HdtPageCaption'), CustomMessage('HdtPageDesc'),
    CustomMessage('HdtPageText'), False, False);
  HdtPage.Add(CustomMessage('HdtOptInstall'));
  HdtPage.Values[0] := True;

  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing),
    SetupMessage(msgPreparingDesc), @OnDownloadProgress);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = HdtPage.ID) and HdtWasInstalled;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  if HdtWasInstalled then
    Result := CustomMessage('HdtFound')
  else if HdtPage.Values[0] then
    Result := CustomMessage('HdtOptInstall')
  else
    Result := '';
  Result := Result + NewLine + NewLine + MemoDirInfo;
end;

procedure InstallHdt;
var
  Setup: String;
  ResultCode: Integer;
begin
  DownloadPage.Clear;
  DownloadPage.Add('{#HdtInstallerUrl}', 'HDT-Installer.exe', '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      MsgBox(FmtMessage(CustomMessage('HdtDownloadFailed'), [GetExceptionMessage]), mbError, MB_OK);
      Exit;
    end;
  finally
    DownloadPage.Hide;
  end;

  Setup := ExpandConstant('{tmp}\HDT-Installer.exe');
  if not Exec(Setup, '', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox(FmtMessage(CustomMessage('HdtRunFailed'), [IntToStr(ResultCode)]), mbError, MB_OK);
    Exit;
  end;

  if not IsHdtInstalled then
    MsgBox(CustomMessage('HdtStillMissing'), mbInformation, MB_OK);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and (not HdtWasInstalled) and HdtPage.Values[0] then
    InstallHdt;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssDone) and (not WizardSilent) then
    MsgBox(CustomMessage('DoneHint'), mbInformation, MB_OK);
end;
