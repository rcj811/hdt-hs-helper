# 爐石輔助 HsHelper

Hearthstone Deck Tracker（HDT）外掛 + 一鍵安裝程式。

## 安裝

到 [Actions](../../actions) 或 [Releases](../../releases) 下載 `HsHelper-Setup-x.y.z.exe`，執行即可：

- **電腦上還沒有 HDT** → 安裝程式會從官方網址下載 HDT 安裝檔並開啟，裝好後接著安裝外掛
- **已經有 HDT** → 自動略過，只安裝外掛

安裝完請重新啟動 HDT，到 **選項 → 追蹤器 → 外掛** 確認「爐石輔助 HsHelper」已啟用。

外掛位置：`%AppData%\HearthstoneDeckTracker\Plugins\HsHelper\HsHelper.dll`

## 功能（v0.1）

進入對戰後，遊戲畫面左側會顯示狀態面板：

- **對戰**：回合、雙方職業／生命／手牌數／牌庫數、場上手下與總攻擊
- **酒館戰棋**：生命、酒館等級、我方陣容（含星級）；戰鬥階段顯示對手英雄、酒館等級與陣容

HDT 選單「外掛」或外掛設定頁的按鈕可以顯示／隱藏面板。

## 開發

- `src/HsHelper/`：外掛原始碼（C#、.NET Framework 4.7.2）
  - `GameStateReader.cs`：從 HDT 讀取遊戲狀態（之後的決策模組也用這份資料）
  - `StatePanel.cs`：覆蓋層面板
- `installer/HsHelper.iss`：Inno Setup 安裝程式
- `scripts/fetch-hdt-refs.ps1`：編譯時從官方 HDT 發行版取出參考檔（不放進 repo）

推到 `main` 會自動編譯；推 `v*` tag（例如 `v0.1.0`）會另外建立 Release。

## 授權說明

HDT 為 HearthSim 版權所有（All Rights Reserved），本專案不包含、也不轉散布任何 HDT 檔案。
