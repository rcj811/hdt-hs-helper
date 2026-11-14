# 爐石輔助 HsHelper

Hearthstone Deck Tracker（HDT）外掛 + 一鍵安裝程式。

## 安裝

到 [Actions](../../actions) 或 [Releases](../../releases) 下載 `HsHelper-Setup-x.y.z.exe`，執行即可：

- **電腦上還沒有 HDT** → 安裝程式會從官方網址下載 HDT 安裝檔並開啟，裝好後接著安裝外掛
- **已經有 HDT** → 自動略過，只安裝外掛

安裝完請重新啟動 HDT，到 **選項 → 追蹤器 → 外掛** 確認「爐石輔助 HsHelper」已啟用。

外掛位置：`%AppData%\HearthstoneDeckTracker\Plugins\HsHelper\HsHelper.dll`

## 自動出牌（v0.2，構築／休閒／練習模式）

> ⚠️ 自動出牌違反暴雪使用條款，可能被封號，請用分身帳號或練習模式。

- **F8**（或 HDT 選單「外掛 → 爐石輔助」）開始／停止；爐石視窗不在最前面時會自動暫停
- 換牌：留 3 費以下；後手且有低費牌時多留一張 4 費
- 每一步都模擬整個回合的出牌＋攻擊順序（深度 12、寬度 200），用評分函式挑最好的一條，只執行第一步後重新讀狀態
- 評分權重參考開源爐石 AI SilverFish（Hearthbuddy 核心）：自己 10 血以下重罰、對手 11 血以下壓血加分、高攻擊敵方手下加重威脅、剩餘法力扣分、下回合可能被斬殺大扣分
- 看不懂的卡牌效果用費用估值；需要目標但看不懂的牌會跳過；動作沒成功會取消並在本回合略過那張牌

### 模擬測試

`tests/HsHelper.Tests` 是決策 AI 的模擬測試（情境測試、400 個隨機局面、200 場模擬對戰），
GitHub Actions 每次都會先跑，**沒全部通過就不會產出安裝檔**。結果在 Actions 頁面的摘要。

## 狀態面板

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

推到 `main` 會自動編譯。要發布新版：改 `HsHelper.csproj` 的 `<Version>`，再到 Actions → Build → Run workflow 勾選「建立 GitHub Release」。

## 授權說明

HDT 為 HearthSim 版權所有（All Rights Reserved），本專案不包含、也不轉散布任何 HDT 檔案。
