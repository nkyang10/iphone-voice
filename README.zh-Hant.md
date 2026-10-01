# Dictation Bridge（繁體中文）

用 iPhone 講嘢，字就直接出現喺 Windows 電腦當前Focused嗰個視窗。

電話用 Web Speech API 聽聲，每講完一句就 POST 俾電腦個小程式，佢用 `SendInput` 逐隻字打出去。
一個全域快捷鍵決定打唔打，所以撳一次 Start 之後，手機就可以擺一邊唔理。

語言版本：[English](README.md) · [繁體中文](README.zh-Hant.md) · [简体中文](README.zh-Hans.md)

![展開並且已啟用嘅 Windows 面板](docs/panel-expanded.png)

![收起時只剩一條細長條](docs/panel-collapsed.png)

![手機上面嗰頁](docs/phone-page.png)

## 佢會產生嘅檔案

所有嘢都放喺 exe 旁邊嘅 `data\` 資料夾，第一次行就會開。exe 本身唔會俾人改。

| 檔案 | 係咩 |
| --- | --- |
| `dictation-bridge.cer` | 要裝去手機嗰張證書，只有公開部分，冇私密鎖匙。 |
| `dictation-bridge.pfx` | 同一張證書連私密鎖匙，行 HTTPS 要用。 |
| `dictation-bridge.log` | 電腦而家做緊咩。 |
| `diagnostics.log` | 手機交嚟嘅報告。去到 4 MB 會改名做 `diagnostics.log.1`。 |
| `dictation-bridge-hotkey.txt` | 你揀嘅快捷鍵。 |
| `dictation-bridge-position.txt` | 面板擺咗喺邊。 |

成個資料夾刪咗就等於全部重設，連證書都冇咗。不過程式行緊嘅時候唔好刪。

## 點用

1. 執行 `DictationBridge.exe`，會彈一個細浮動面板，右下角亦有圖示。
2. 用手機開佢顯示嘅網址，例如 `https://192.168.1.162:8080/`。
3. 撳一次 **Start listening**，如果問就俾麥克風權限。
4. 之後直接講就得。喺電腦按 **Ctrl+Alt+D** 開始／停止打字。

面板紅色寫住 `DISARMED` 就代表未啟用，係刻意咁：程式開機一定係停用，就算重啟都唔會
啱啱好打落你唔想佢打字嗰個視窗。停用期間講嘅嘢會暫存，一啟用就即刻打出去。

### 第一次要做嘅證書設定

iOS 淨係喺安全頁面先會開放語音辨識，所以程式用自簽憑證行 HTTPS。iOS Safari 冇
macOS Safari 嗰個「繼續前往」掣，所以憑證要裝一次：

1. 將 `data\dictation-bridge.cer` 傳去手機，撳開佢。
2. 設定 > 一般 > VPN 與裝置管理 > 撳個描述檔 > 安裝。
3. 設定 > 一般 > 關於本機 > 信任證書設定 > 開啟佢。
4. 開步驟 2 嗰個網址。

**淨係要做一次。** 電腦會保留張證書，下次開程式直接沿用，所以重開程式或者重啟電腦都
唔使再裝。

張證書同時有 `dictation-bridge.local` 呢個名喺入面。如果手機解析得到，可以改用
`https://dictation-bridge.local:8080/`，同一張證書就換咗網絡都一樣用得着。不一定每個
網絡都解析得到，所以 IP 嗰個網址最穩，名只係額外着數。

如果地址變成證書冇列嗰個，log 會寫 `issuing a new one`，咁就要再裝多次新嘅
`dictation-bridge.cer`。净係呢種情況要重做。

## 點解 iOS 要做呢啲

`SpeechRecognition` 只喺安全來源先開放。喺 `http://` 頁面，Safari 根本唔會俾呢個 API
你，冇錯可以撳落去，只係話你冇語音辨識。憑證就係為咗過呢一關，本身唔係用嚟驗證身份。

仲有兩個限制影響咗設計：

- **第一次 `start()` 要撳一下**，因為 iOS 要靠用戶操作先彈出麥克風權限查詢。之後嘅重啟
  由語音辨識自己嘅 `onend` 觸發，唔使再撳。
- **靜咗一段時間，Safari 會自己結束今次辨識**，程式會自動重啟。不過如果係你嗰個 iOS
  版本要求**每次** `start()` 都要撳，計時器觸發嘅重啟就會靜靜哋失敗。診斷報告會記錄
  每次 `start()` 嘅 `gesture=true/false`，睇吓你係邊種情況。

## 需要乜嘢

- Windows 10 或以上，64 位元。乜都唔使裝：個 exe 只引用 Windows 本身一定有嘅
  .NET 4.0 組件。
- iOS 14.5 以上嘅 iPhone／iPad，用 Safari。iOS 上嘅 Chrome 同 Firefox 底層都係
  WebKit，行為一樣，不過我測試過嘅路徑係 Safari。
- 同一個網絡。手機要經你屋企個 LAN 連到電腦。

## 個浮動面板

細細粒，always-on-top，可以拖到邊都得。一行係狀態，撳一下展開睇全部。

一行有：可以撳嘅 **ARMED / DISARMED** 掣、電話連咗未嘅狀態，同埋展開掣。拖條
或者面板任何空白位就郁得。收埋就係收埋，唔會喺工作列出現；右邊圖示可以叫返佢出嚟。

展開之後多咗：手機網址、最後打咗嘅嘢、暫存清單，同埋改快捷鍵／清空／複製／離開嘅掣。

### 改快捷鍵

預設 **Ctrl+Alt+D**。喺展開面板撳快捷鍵嗰個掣，按你想用嘅組合，即時生效。呢個選擇
會記低喺 exe 旁邊嘅 `dictation-bridge-hotkey.txt`，重開都會記得。F12 唔會畀綁，因為
Windows 留咗畀 debugger。如果個組合已經俾另一個程式用咗，會拒絕綁定、還原舊嗰個，
面板亦都會講。

如果 8080 埠俾人用咗，改埠：`DictationBridge.exe --port 8099`。

## 診斷

電話會將每個語音辨識事件連時間戳記低，可以 POST 去 exe 旁邊嘅 `diagnostics.log`。
全部喺你自己部機，唔使帳戶、唔使服務、唔使錢。

喺電話嗰頁撳 **Send to desktop** 就會交報告。如果語音辨識出唔到聲又冇錯誤，
亦會自動交，等一個只喺真機重現嘅問題都有得查。

**Include spoken words in the report** 預設關咗。診斷只會記「聽唔到」，唔會記你講過
乜；除非你想埋啲字入報告，先至開返佢。

`diagnostics.log` 上限 4 MB，滿咗會改名做 `diagnostics.log.1`。

## 疑難排解

**頁面話冇語音辨識**：憑證未受信任。檢查「信任證書設定」，同埋確認你係用 `https://`
而唔係 `http://`。

**開始好用，閒咗一陣之後靜咗**：睇 `diagnostics.log` 入面嘅 `gesture=false`。如果每次
計時器重啟都係 `gesture=false`，之後跟住 `startThrew`，即係你個 iOS 每次 `start()`
都要撳。

**頁面收到字，但電腦冇反應**：語音辨識正常，係傳輸有問題。狀態嗰行會寫住佢試過嘅埠。

**憑證成日變**：電腦 IP 唔喺憑證 SAN 入面就會重新產生，要重裝新嘅 `.cer`。通常唔會
發生，因為 DHCP 多數派返同一個地址。

**完全唔打字**：檢查目標程式係咪用管理員身份執行。`SendInput` 注入唔到高權限視窗，
log 會講得好清楚。

**頁面停咗**：iPhone 熄咗屏幕就會停。將「自動鎖定」設成「永不」。

## 已知限制

- 屏幕要保持亮着同埋未鎖；自動鎖定設永不。
- 打唔入以管理員身份執行嘅程式。
- 電腦地址一變就要重裝憑證。
- 粵語用 `zh-HK`，如果裝置唔收就依次試 `yue-HK`、`zh-TW`、`en-US`。頁面會顯示最後
  用咗邊個。
- 同一個網絡上如果有人知道咗 token，就可以打落你個視窗。屋企 LAN 冇問題，共用就唔好。

## 編譯

```powershell
.\build.ps1
```

用 Windows 本身嗰個 `Microsoft.NET\Framework64` 入面嘅 `csc.exe` 嚟編譯，所以唔使
裝 SDK、唔使上網。網頁會嵌入成資源，所以個 exe 係單一檔案；旁邊亦會放一份副本方便改。

改 `web\index.html` 然後重新編譯。項目慣例見 `AGENTS.md`。

## 授權

MIT。