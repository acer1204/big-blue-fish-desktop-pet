# 蓝色大肥鱼桌宠 / Big Blue Fish Desktop Pet

> [!NOTE]
> **這是台灣版修改分支（`taiwan-edition`），不是原作者的官方版本。**
>
> 本分支由 acer1204 於 2026 年 10 月 26 日，以原作者 [Bai-Cute/big-blue-fish-desktop-pet](https://github.com/Bai-Cute/big-blue-fish-desktop-pet) 的 main 分支 commit `5eead98`（1.0.1 版標籤 `v1.0.1` 之後，只多了一個補上 LICENSE 著作權欄位的修正）為基礎修改。下方「台灣版修改說明」列出主要變更與所有修改過的檔案；說明結束處的分隔線以下是原作者的說明（簡體中文），描述的是原版的行為。

## 台灣版修改說明

依 [Apache License 2.0](LICENSE) 第 4 條第 (b) 款，本節列出這個分支新增、刪除或修改的檔案，以及修改者與修改日期。修改過的程式碼、專案檔與設定檔（.cs、.xaml、.csproj、.ps1、.gitattributes，共 59 個）第一行都加註了修改聲明；`README.md` 與建置安裝檔時會一併打包的 [`code/THIRD-PARTY.md`](code/THIRD-PARTY.md) 則在開頭段落註明這是修改版。原作者的著作權聲明（Copyright 2026 Bai-Cute）與授權條款維持不變，本分支的修改同樣以 Apache-2.0 授權釋出。本分支並非由原作者維護，修改內容也未經原作者審閱或背書；使用時遇到問題，請到本分支的儲存庫 [acer1204/big-blue-fish-desktop-pet](https://github.com/acer1204/big-blue-fish-desktop-pet/issues) 回報，不要回報給原作者。

### 主要變更

- **動畫更順暢**：桌寵顯示期間把系統計時器解析度調到 1 毫秒（隱藏時釋放），讓每一幀都在預定時間顯示，避免幀間隔在約 31 與 47 毫秒之間跳動。點選與拖曳用到的 6 個動作會在啟動後預先解碼開頭 6 幀（約多佔用 33 MB 記憶體）；在修改者 acer1204 的電腦上，放開滑鼠或開始拖曳後約 10 毫秒就會出現第一幀（原本約 60–90 毫秒），實際時間依電腦而異。其他動作切換時仍需等待約 50–90 毫秒。
- **可使用遠端模型**：設定頁「模型與供電」新增「模型來源」，可以改用遠端的 llama.cpp 伺服器（OpenAI 相容介面），填入網址、API Key 與模型名稱，也提供「儲存並測試連線」按鈕。使用遠端模型時，會傳送到該伺服器的內容包括：視窗文字（OCR 辨識結果）或視窗截圖、前景視窗的程式名稱、標題、視窗類別與介面元件名稱、目前時間，以及開啟天氣和新聞時的天氣地點與預報、新聞內容。原本的「啟用本機大模型」開關改名為「啟用模型說話」：它是讓魚魚用模型說話的總開關，使用遠端模型時也要開著，而且不會在本機載入模型；「拔掉電源時暫停模型並釋放記憶體」與「使用顯示卡加速」只對本機模型有效，使用電池時遠端模型會繼續執行。
- **直接看截圖**：遠端伺服器在 `/v1/models` 回報該模型支援圖片輸入，並且勾選了「模型支援圖片時，直接傳送視窗截圖」時，才會改傳視窗截圖（長邊最多 1568 像素），不再傳 OCR 文字；截圖失敗或伺服器沒有回報支援圖片時，預設的 OCR 版會自動改回 OCR（以 Vision 模式自行編譯的版本沒有 OCR，只會傳視窗名稱等文字資訊）。
- **繁體中文與台灣用語**：Windows 版介面、與主程式共用的模型安裝和更新視窗、MCP 工具說明與同意視窗，以及魚魚的說話語氣，都改為台灣繁體中文與台灣用語。氣泡字型由不含繁體字的「乐米小奶泡体」改為 [jf open 粉圓 2.1](https://github.com/justfont/open-huninn-font)（SIL OFL 1.1）；設定視窗、右鍵選單、系統匣選單與共用視窗改用微軟正黑體。106 個動作名稱同時用作動畫檔名與 MCP 動作名稱，因此仍維持原本的簡體中文。
- **台灣天氣**：Windows 版的天氣地點改為「縣市 → 鄉鎮市區」兩層選單（22 個縣市、368 個鄉鎮市區），新安裝的預設地點為臺北市中正區。天氣資料改用[中央氣象署開放資料](https://opendata.cwa.gov.tw/)的鄉鎮天氣預報，需要自行申請並在設定中填入授權碼；填入之前，台灣地點（包括預設的臺北市中正區）不會有天氣資料，也不會改用 Open-Meteo。原本的 Open-Meteo 天氣仍保留給非台灣地點（先前儲存或透過 MCP 設定的）與 Linux 版使用。Linux 版沒有授權碼欄位，所以不會出現本分支新增的氣象署縣市清單，MCP 也不能把 Linux 版設成台灣鄉鎮；原有地區資料中的「台湾省」仍會出現，但和原版一樣沒有座標，選了也不會有天氣資料。
- **金鑰保護**：遠端 API Key 與氣象署授權碼以目前的 Windows 帳戶加密（DPAPI）後儲存，而且不會出現在 MCP 的 `get_settings` 結果中。這可以防止設定檔被複製到其他帳戶或電腦後讀出金鑰，但同一個 Windows 帳戶下執行的其他程式仍然可以解密。
- **測試同步**：`code/tests/` 中比對介面文字的測試程式也隨之改為繁體中文。
- **氣泡不出現簡體字**：Windows 版魚魚的氣泡文字（自己的模型、天氣、MCP 的 `say`／`say_next`、Hermes 橋接）在顯示前都會經過新增的 `CompanionTraditional`，用 [OpenCC](https://github.com/BYVoid/OpenCC) 1.4.2 的字典把簡體字轉成台灣繁體。整段都是簡體時，結果和 OpenCC 的 `s2twp` 相同（包括軟件→軟體這類台灣用語），另外修正了 OpenCC 會轉錯的「一起干杯」（幹杯）與「的士兵、的士林」（計程車）；繁體句子裡夾雜的簡體字只改那幾個字，原本就是繁體或英文的內容維持不變。模型邊產生邊顯示時，需要轉換的句子會等最後一段文字完整再顯示，避免轉到一半的字一閃而過。已知的取捨：為了修正常見的「以后、然后」，罕見用法「選后」也會變成「選後」。MCP 工具回傳給 Agent 的仍是原文。新增的 `code/tests/TraditionalTextVerification` 以 8591 筆語料驗證轉換結果。
- **Hermes 對話橋接**：新增 `code/BigBlueFish.HermesBridge`（C#）與 `code/run-hermes-bridge.ps1`。雙擊魚魚在輸入框打的字，會交給本機的 [Hermes Agent](https://github.com/NousResearch/hermes-agent) API 回答，回答再顯示在魚魚的氣泡裡。等待時魚魚會播放思考動作，氣泡也會即時顯示 Hermes 正在做什麼（例如「正在上網搜尋：…」「正在電腦上執行指令…」）和已經過的時間；MCP 外掛工具的進度會依伺服器與工具名稱自動產生，例如「正在用 Divebird 查詢下載」，之後新增的 MCP 伺服器也適用；同一個動作連續重複時會顯示次數（例如「第 12 次」），看得出 Hermes 是在重試而不是當掉。工作進行中輸入 `/stop` 會請 Hermes 立刻停止（連同它開出來的背景程序）；這時送出其他訊息，魚魚會先回覆一句，等目前的工作結束再處理。外部 Agent 的氣泡還在顯示時，魚魚不會自己開口把它蓋掉。Hermes 預設不會把 MCP 外掛工具全部直接列給模型，所以提示詞會要求它需要工具時先用 `tool_search` 查找、找到合適的外掛工具就優先使用，下載檔案或影片時優先使用 Divebird 下載管理員，下載前先核對標題或網址；同一個指令或工具失敗兩次就換方法，失敗三次就停下來詢問。同一段對話會延續上下文，連重新啟動橋接程式後也接得上；在輸入框打 `/new` 會開始新的對話，但 Hermes 寫進長期記憶的內容（例如被要求「記住」的事）不會因此清掉。

### 使用 Hermes 對話橋接

1. 桌寵設定的「Agent 與 MCP」開啟「開啟 MCP 介面」與「雙擊與魚魚對話」。不需要開啟「提線木偶模式」；開了的話魚魚會停止自己說話和切換動作，橋接程式關閉後也要手動關掉才會恢復。
2. Hermes 的 `.env` 設定一組 16 字元以上的 `API_SERVER_KEY`，重新啟動 Hermes gateway（`hermes gateway restart -y`），讓 API 介面在 `http://127.0.0.1:8642` 啟動。
3. 執行 `./code/run-hermes-bridge.ps1`（會先編譯再啟動）。橋接程式會自動尋找 `BigBlueFish.Mcp.exe`，並從 Hermes 的 `.env` 讀取金鑰，不會把金鑰寫到其他地方或顯示出來。
4. 雙擊魚魚輸入訊息，按「送出」或 Ctrl+Enter。輸入 `/new` 開始新的對話，輸入 `/stop`（或 `/停止`）停止目前的工作。

在 Hermes 新增 MCP 伺服器後，Hermes gateway 會自動載入，不必重新啟動橋接程式；如果 Hermes 在先前的對話裡說過找不到某個工具，可以在輸入框打 `/new` 開始新的對話，或在訊息裡直接點名要用的工具。

注意：Hermes 回答時可以使用它所有的工具，包括在這台電腦執行終端機指令與修改檔案；能在魚魚輸入框打字的人，就能請 Hermes 做這些事。橋接程式啟動時若發現 Hermes 開著這類工具組，會先顯示提醒。訊息也會送到 Hermes 設定的模型服務。請只在自己的電腦上使用，並視需要在 Hermes 的設定中限制 API 平台可用的工具。

### 新增或移除的第三方內容

- 新增 jf open 粉圓 2.1 字型（未修改）：著作權屬 justfont Co., LTD.，以 SIL Open Font License 1.1 授權；字型內含 Varela Round（SIL OFL 1.1）的拉丁字母，漢字部分衍生自 Kosugi Maru（Apache-2.0）。字型授權全文在 `code/licenses/jf-open-huninn-OFL.txt`，Apache-2.0 全文在 `code/licenses/Apache-2.0.txt`。
- 移除「乐米小奶泡体」字型及其授權說明，本分支不再散布該字型。
- 新增 OpenCC 1.4.2 字典（Apache-2.0，著作權屬 Carbo Kuo（BYVoid）與 OpenCC 貢獻者），放在 `code/BigBlueFish.Windows/assets/opencc/` 並嵌入程式：`STCharacters`、`STPhrases`、`TSCharacters`、`TWPhrases`、`TWVariants`、`TWVariantsPhrases` 與官方 `ver.1.4.2` 的原檔完全相同；`STPhrases_GeneratedFromRegionalPhrases.txt` 是 OpenCC 建置時產生的檔案，取自官方 Python 套件並加上註明來源的檔頭。授權全文在 `code/licenses/OpenCC-Apache-2.0.txt`，來源與 SHA-256 雜湊值記錄在同資料夾的 `source.json`。
- 台灣天氣資料只在執行時向中央氣象署查詢，不隨程式附帶天氣資料。顯名聲明：中央氣象署 2026 鄉鎮天氣預報（F-D0047）。此開放資料依政府資料開放授權條款 (Open Government Data License) 進行公眾釋出，使用者於遵守本條款各項規定之前提下，得利用之。政府資料開放授權條款：https://data.gov.tw/license 。中央氣象署並未推薦或背書本程式。
- 台灣縣市與鄉鎮市區清單由新增的 `code/tools/import-taiwan-regions.py` 產生：從原有的 AreaCity 地區資料（MIT）取出台灣部分，以 OpenCC 轉換為繁體中文；金門縣與連江縣的鄉鎮因為 AreaCity 歸在福建省之下，直接寫在腳本中。
- 版本、SHA-256 雜湊值與授權的詳細資訊已更新於[第三方聲明](code/THIRD-PARTY.md)（英文）。

### 尚未修改的部分

- 安裝程式與解除安裝程式（`installer/`、`uninstaller/`）自己的文字，以及 Linux 版的設定、選單等介面仍為簡體中文；但它們共用的視窗框架、模型下載視窗、模型狀態與錯誤訊息，以及魚魚的回覆已改為繁體，所以畫面會繁簡混雜。Linux 版的氣泡字型也已改為 jf open 粉圓，對話框字型改為 Noto Sans CJK TC。Linux 版的氣泡沒有加上簡體轉繁體，也不會在外部 Agent 說話時暫停自己說話。Linux 版可以編譯，但沒有實際執行驗證。
- 執行檔的產品名稱與檔案描述（工作管理員中顯示的名稱）、MCP 橋接程式 `BigBlueFish.Mcp` 的連線錯誤訊息，以及 `tools/` 底下建置與模型準備腳本的訊息仍為簡體中文（`Model-Helpers.ps1` 的模型來源名稱與錯誤訊息已改為繁體）。
- 安裝後在 Windows「已安裝的應用程式」顯示的發行者與網址、Linux 套件的維護者與首頁，以及「檢查更新」的來源，目前仍指向原作者 Bai-Cute 及其儲存庫。在這些資訊改為指向本分支之前，本分支不提供編譯好的安裝檔，以免被誤認為原作者發行，或被「檢查更新」換回原版。
- 本 README 分隔線以下的原作者說明、`docs/` 規格文件、`CHANGELOG.md`，以及建置安裝檔時附帶的 `release/README.md`（安裝說明）仍是原作者的內容，描述的是原版行為；其中「开源许可与第三方权利信息」一節仍列出原版的「乐米小奶泡体」字型，本分支實際附帶的字型與資料來源，請以上方「新增或移除的第三方內容」與 `code/THIRD-PARTY.md` 為準。
- 科技新聞來源仍是 IT之家，提示詞會要求模型改寫成台灣繁體中文。

### 修改過的檔案

以下 100 個檔案由 acer1204 於 2026 年 10 月 26 日新增、刪除或修改（相對於原作者儲存庫的 commit `5eead98`）。完整差異可以用 `git diff 5eead98 taiwan-edition` 檢視。

<details>
<summary>展開檔案清單</summary>

**新增（37）**

- `code/BigBlueFish.HermesBridge/BigBlueFish.HermesBridge.csproj`
- `code/BigBlueFish.HermesBridge/Bridge.cs`
- `code/BigBlueFish.HermesBridge/HermesChat.cs`
- `code/BigBlueFish.HermesBridge/PetLink.cs`
- `code/BigBlueFish.HermesBridge/Program.cs`
- `code/BigBlueFish.HermesBridge/ToolStatus.cs`
- `code/BigBlueFish.HermesBridge/packages.lock.json`
- `code/BigBlueFish.Windows/Companion/CompanionTraditional.cs`
- `code/BigBlueFish.Windows/Res/Font/jf-openhuninn-2.1.ttf`
- `code/BigBlueFish.Windows/assets/opencc/STCharacters.txt`
- `code/BigBlueFish.Windows/assets/opencc/STPhrases.txt`
- `code/BigBlueFish.Windows/assets/opencc/STPhrases_GeneratedFromRegionalPhrases.txt`
- `code/BigBlueFish.Windows/assets/opencc/TSCharacters.txt`
- `code/BigBlueFish.Windows/assets/opencc/TWPhrases.txt`
- `code/BigBlueFish.Windows/assets/opencc/TWVariants.txt`
- `code/BigBlueFish.Windows/assets/opencc/TWVariantsPhrases.txt`
- `code/BigBlueFish.Windows/assets/opencc/source.json`
- `code/BigBlueFish.Windows/assets/regions/taiwan-regions.json`
- `code/licenses/OpenCC-Apache-2.0.txt`
- `code/licenses/jf-open-huninn-OFL.txt`
- `code/run-hermes-bridge.ps1`
- `code/tests/TraditionalTextVerification/Program.cs`
- `code/tests/TraditionalTextVerification/TraditionalTextVerification.csproj`
- `code/tests/TraditionalTextVerification/corpora/mixed_cases.expected.txt`
- `code/tests/TraditionalTextVerification/corpora/mixed_cases.txt`
- `code/tests/TraditionalTextVerification/corpora/simplified_upstream.expected.txt`
- `code/tests/TraditionalTextVerification/corpora/simplified_upstream.txt`
- `code/tests/TraditionalTextVerification/corpora/traditional_app.expected.txt`
- `code/tests/TraditionalTextVerification/corpora/traditional_app.txt`
- `code/tests/TraditionalTextVerification/corpora/traditional_stress.expected.txt`
- `code/tests/TraditionalTextVerification/corpora/traditional_stress.txt`
- `code/tests/TraditionalTextVerification/corpora/tw_regions_mixed.expected.txt`
- `code/tests/TraditionalTextVerification/corpora/tw_regions_mixed.txt`
- `code/tests/TraditionalTextVerification/corpora/tw_regions_traditional.expected.txt`
- `code/tests/TraditionalTextVerification/corpora/tw_regions_traditional.txt`
- `code/tests/TraditionalTextVerification/packages.lock.json`
- `code/tools/import-taiwan-regions.py`

**刪除（2）**

- `code/BigBlueFish.Windows/Res/Font/LemiXiaoNaiPaoTi.ttf`
- `code/licenses/Lemi-Xiao-Nai-PaoTi.txt`

**修改（61）**

- `.gitattributes`
- `README.md`
- `code/BigBlueFish.Control/ControlServer.cs`
- `code/BigBlueFish.Control/ControlTools.cs`
- `code/BigBlueFish.Linux/BigBlueFish.Linux.csproj`
- `code/BigBlueFish.Linux/LinuxMcp.cs`
- `code/BigBlueFish.Linux/LinuxPetWindow.cs`
- `code/BigBlueFish.Linux/LinuxSettings.cs`
- `code/BigBlueFish.Windows/BigBlueFish.Windows.csproj`
- `code/BigBlueFish.Windows/Companion/CompanionAnimationPolicy.cs`
- `code/BigBlueFish.Windows/Companion/CompanionAnimations.cs`
- `code/BigBlueFish.Windows/Companion/CompanionBrain.cs`
- `code/BigBlueFish.Windows/Companion/CompanionDialogue.cs`
- `code/BigBlueFish.Windows/Companion/CompanionEdgeLayout.cs`
- `code/BigBlueFish.Windows/Companion/CompanionJob.cs`
- `code/BigBlueFish.Windows/Companion/CompanionMaintenance.cs`
- `code/BigBlueFish.Windows/Companion/CompanionMcp.cs`
- `code/BigBlueFish.Windows/Companion/CompanionMenuStyles.xaml`
- `code/BigBlueFish.Windows/Companion/CompanionMenus.cs`
- `code/BigBlueFish.Windows/Companion/CompanionNative.cs`
- `code/BigBlueFish.Windows/Companion/CompanionNews.cs`
- `code/BigBlueFish.Windows/Companion/CompanionOcr.cs`
- `code/BigBlueFish.Windows/Companion/CompanionOcrLayout.cs`
- `code/BigBlueFish.Windows/Companion/CompanionPreferences.cs`
- `code/BigBlueFish.Windows/Companion/CompanionRegions.cs`
- `code/BigBlueFish.Windows/Companion/CompanionScreenCapture.cs`
- `code/BigBlueFish.Windows/Companion/CompanionSettings.cs`
- `code/BigBlueFish.Windows/Companion/CompanionSpeech.cs`
- `code/BigBlueFish.Windows/Companion/CompanionTrayIcon.cs`
- `code/BigBlueFish.Windows/Companion/CompanionTrayMenuRenderer.cs`
- `code/BigBlueFish.Windows/Companion/CompanionVideoPlayer.cs`
- `code/BigBlueFish.Windows/Companion/CompanionWeather.cs`
- `code/BigBlueFish.Windows/Companion/CompanionWindow.cs`
- `code/BigBlueFish.Windows/MainWindow.xaml`
- `code/THIRD-PARTY.md`
- `code/test.ps1`
- `code/tests/AnimationUiVerification/Program.cs`
- `code/tests/ForegroundVerification/Program.cs`
- `code/tests/InstallerVerification/DeploymentVerification.cs`
- `code/tests/InstallerVerification/DownloadInterruptionVerification.cs`
- `code/tests/McpProtocolVerification/Program.cs`
- `code/tests/McpUiVerification/Program.cs`
- `code/tests/OcrLayoutVerification/Program.cs`
- `code/tests/OcrVerification/Program.cs`
- `code/tests/PowerNoticeVerification/Program.cs`
- `code/tests/RunnerVerification/LatencyVerification.cs`
- `code/tests/RunnerVerification/MaintenanceVerification.cs`
- `code/tests/RunnerVerification/NewsVerification.cs`
- `code/tests/RunnerVerification/PerformanceVerification.cs`
- `code/tests/RunnerVerification/PersonaVerification.cs`
- `code/tests/RunnerVerification/Program.cs`
- `code/tests/RunnerVerification/SpeechFlowVerification.cs`
- `code/tests/RunnerVerification/TrayVerification.cs`
- `code/tests/SafeEditionVerification/Program.cs`
- `deployment/ArtifactDownload.cs`
- `deployment/ModelInstallWindow.cs`
- `deployment/ModelStore.cs`
- `deployment/ModernUI.cs`
- `deployment/UpdateService.cs`
- `deployment/UpdateWindow.cs`
- `tools/Model-Helpers.ps1`

</details>

---

蓝色大肥鱼是一款住在电脑桌面上的 AI 桌宠软件，支持 Windows 与 Linux 双平台。她是一位蓝发的小鲸鱼娘，形象来自对 DeepSeek 的二创虚拟形象“蓝色大肥鱼”。软件内置 4B 本地大模型，借助 OCR 技术读懂主人正在桌面上做的事情，每隔一段时间就从文字气泡里送来一句可爱的话，陪伴和关心主人；她还会查看天气、浏览科技新闻，再用自己的方式讲给主人听。

鱼鱼准备了一百多种可爱的动作表情，平时会在桌面上呼吸、哼歌、伸懒腰、吃零食、玩耍，到了饭点会认真吃饭，到了节日会换上应景的小活动，被点一下、被拎起来时也有专门设计的反应。每次启动，她都会提起裙摆向主人行礼请安；关闭时，也会用同样的礼仪和主人道别。

当前版本为 **1.0.1**，安装包请前往 [GitHub Releases](https://github.com/Bai-Cute/big-blue-fish-desktop-pet/releases/latest) 下载。

<p align="center">
  <img src="docs/images/bigbluefish-rest-transparent.png" width="38%" alt="蓝色大肥鱼休息状态" />
  <img src="docs/images/bigbluefish-speaking-transparent.png" width="58%" alt="蓝色大肥鱼吐字状态" />
</p>

## 认识鱼鱼

鱼鱼有一头柔软的蓝色长发，头顶戴着蕾丝女仆发饰，耳边是小小的鲸鳍，身后摇着一条鲸鱼尾巴。她穿着深蓝色的女仆装，白色围裙上印着一只小鲸鱼，整个人看起来就像一位住进电脑里的小鲸鱼娘。她平时安静地待在桌面边缘，身边透明的地方可以直接点到后面的桌面，主人也可以把她拖到任何喜欢的位置，大小可以在 50% 到 200% 之间调整。

她说话的语气亲切温柔，又带着一点可爱俏皮。看见主人在写代码，她会摇摇尾巴为认真工作的主人加油；看见主人和朋友约明天吃饭，她会忍不住对好吃的动一点小心思；天气变冷或者主人忙了很久，她也会轻轻地送来一句关心。启动时，她会按照时间说“主人早上好，鱼鱼给主人请安啦～”；道别时，她会说“主人再见～鱼鱼喜欢主人哦”。

<p align="center">
  <img src="docs/images/bigbluefish-maid-bow.gif" width="32%" alt="女仆屈膝礼仪" />
  <img src="docs/images/bigbluefish-idle-breath.gif" width="32%" alt="待机呼吸休闲" />
  <img src="docs/images/bigbluefish-click-wave.gif" width="32%" alt="点击回应元气挥手" />
</p>

## 她怎样陪伴主人

鱼鱼会读取当前前台窗口的画面，通过 OCR 识别其中的文字，并按照标题栏、侧边栏、正文等区域整理好，再交给内置的 4B 本地模型理解。无论主人在读文章、写文档、改代码，还是在聊天里提到了什么话题，她都能顺着眼前的内容组织一小段新的话；主人停在桌面、主页或新标签页时，她也会自然地问候、撒娇或者碎碎念。Windows 使用系统自带的 Windows OCR，Linux 使用 Tesseract，X11 与 Wayland 桌面均可使用。

启动约 45 秒后，鱼鱼会第一次开口，之后在主人选择的时间范围内随机安排下一次说话，范围从 2–3 分钟到 20–30 分钟共六档。她会挑主人暂时停下键盘和鼠标的间隙开口，准备说话时头边会转起一个蓝色的小圈圈，随后文字一个字一个字地出现在奶油色的气泡里。气泡大小和停留时间都可以在设置中调整，右键菜单里的“吐字测试”可以随时让她现场说一句。

主人可以按省、市、区县三级选择天气地点。鱼鱼每小时会悄悄更新一次天气，播报时结合气温、体感、湿度、风力、最高最低温和降水概率，用鲸鱼娘的口吻讲给主人听；右键菜单里的“天气”可以随时让她汇报。她也会从 IT之家浏览最新的科技新闻，挑一条讲清楚要点，再加上一小句自己的感受。

内置模型每次只进行一次单独的输出，没有对话历史，也不保留记忆。读屏内容只在本机交给内置模型使用，不会被上传到云端，也不会保存到本地。

笔记本电脑离开电源时，鱼鱼可以自动停止模型推理并把模型移出内存，帮主人照顾续航；外部程序全屏时，她会自动躲起来，结束后再回到桌面。这些行为都可以在设置中按自己的习惯调整。

## 一百多种动作表情

鱼鱼内置 106 段透明动画，覆盖待机、点击、拖动、日常事件、用餐、季节和节日等场景。每一种动作都有自己的出现权重，并会结合当前时间、日期、季节、饭点和主人的操作实时计算，这些概率都经过细致严谨的微调，让她的表情丰富有趣，又贴合主人当下的生活节奏。每段动作在没有新的交互打断时都会完整播放，再自然地衔接到下一段。

<p align="center">
  <img src="docs/images/bigbluefish-meal.gif" width="32%" alt="用餐动作" />
  <img src="docs/images/bigbluefish-fireworks.gif" width="32%" alt="节日烟花动作" />
  <img src="docs/images/bigbluefish-sleep.gif" width="32%" alt="夜间小憩沉眠" />
</p>

待机时，鱼鱼会在好几种安静的动作之间轮换，轻轻地呼吸、悠闲地哼歌；到了晚上 23 点之后，她会开始犯困，时不时打个小盹，直到第二天早上 8 点。主人点击她时，她会从好几种回应里挑一种，可能元气地挥挥手，可能害羞地吓一跳，也可能露出别的小表情，有些回应要多点几次才容易遇到。把她拎起来拖动时，她会悬在半空中保持被抓住的小模样，直到主人松手。

每隔一段时间（默认 40 秒，可在 30 秒到 5 分钟之间调整），鱼鱼会从大量日常事件中挑一件小事来做：跳舞、吹泡泡、敲小电脑，等等许多许多精美华丽的动作。三餐的时间可以在设置中按主人的作息填写，饭点前后一小时里，她会更常端出好多种美食和小吃。早上她更容易打哈欠，每天晚上 23 点后有空的时候，还会认真地刷一次牙。

节日是鱼鱼特别用心准备的部分。春节、元宵、情人节、520、端午、七夕、中秋、万圣节、圣诞节等节日前后，她都准备了专属的节日动作，比如春节写福字、放烟花，中秋赏月吃月饼，有些动作还会随着节日临近越来越常见；节日前后的饭点，她也会端出应景的节日美食。农历节日按照农历日期计算，闰月也能正确处理。

季节也会改变她的日常：冬天会堆雪人，秋天会被落叶埋住，夏天还有好几种消暑的小活动。她偶尔也会跑一跑、学螃蟹横着走，跑出一小段距离，在那里待一会儿，再回到原来的位置。

除了这些，鱼鱼还藏着许多没有写在这里的小心思。愿主人在每天的陪伴里慢慢发现她的可爱，每一次不经意的抬头，都能撞见一个小小的惊喜。

## 安装

普通用户请从 [GitHub Releases](https://github.com/Bai-Cute/big-blue-fish-desktop-pet/releases/latest) 下载对应系统的安装包。每个平台都提供标准版和无MCP版，文件名中带 `Safe` 的是无MCP版，各安装包都附有配套的 `.sha256` 校验文件。

| 平台 | 标准版 | 无MCP版 |
| --- | --- | --- |
| Windows x64 | `BigBlueFish-v1.0.1-OCR-Setup.exe` | `BigBlueFish-v1.0.1-Safe-OCR-Setup.exe` |
| Ubuntu／Debian x64 | `BigBlueFish-v1.0.1-Linux-x64.deb` | `BigBlueFish-v1.0.1-Safe-Linux-x64.deb` |

在 Windows 上，双击安装器即可开始部署。安装器会把桌宠程序、动画资源、.NET 运行时和本地推理运行时一并安装好，默认位置是 `Program Files` 下的“蓝色大肥鱼”文件夹，并创建桌面和开始菜单快捷方式。随后安装器会请主人选择模型的准备方式：Hugging Face（推荐）、国内源（稳定）或手动安装。自动下载的模型约 2.7 GB，下载完成后会经过 SHA-256 校验，中途断开也能从已有进度继续。下载时如果想改用手动安装，可以选择“放弃并转为手动安装”；手动安装会显示模型应放入的文件夹，并打开模型来源页面，也可以直接选择已经下载好的 GGUF 文件。卸载时可以在卸载界面勾选“保留模型和设置”。

在 Ubuntu／Debian 上，下载对应的 `.deb` 后，在其所在目录执行：

```bash
sudo apt install ./BigBlueFish-v1.0.1-Linux-x64.deb
```

安装无MCP版时换成对应的文件名即可。安装完成后，可以从应用菜单或 `bigbluefish` 命令启动鱼鱼，再到设置的“模型与信息”中点击“安装模型”，为她准备本地模型。

如果暂时不想下载模型，也可以先让鱼鱼以无模型模式住进桌面。这时她依然会行礼、问候、道别，播放全部动作表情，回应主人的点击和拖动。之后随时可以到设置中点击“安装模型”，让她开始读懂屏幕、开口说话。

鱼鱼可以在设置中检查更新，也可以在每次启动时自动检查。发现新版本后，应用会下载并校验对应版本的安装包，完成更新后重新启动，模型和设置都会继续保留。Windows 用户也可以退出桌宠后手动运行新版安装器，选择原安装目录完成更新。

## 内置模型

鱼鱼使用 Qwen3.5-4B 的 Q4_K_M 量化 GGUF 模型，由随包分发的 [llama.cpp](https://github.com/ggml-org/llama.cpp)（b10809，Vulkan 版本）在本机推理，可以使用 GPU 加速，也可以切换到 CPU 运行。两个预设来源的模型有所不同，但都适合本应用，主人可以按自己的网络环境选择。

Hugging Face 来源为 [Biomanticus/Qwen3.5-4B-heretic-gguf](https://huggingface.co/Biomanticus/Qwen3.5-4B-heretic-gguf/tree/b63ff4662e4863cfa005c9cd8ed34b87ed44b7e1)，它是 [p-e-w/Qwen3.5-4B-heretic](https://huggingface.co/p-e-w/Qwen3.5-4B-heretic) 的 GGUF 转换版本，基于 Qwen3.5-4B。国内来源为 ModelScope 上的 [diodel/Qwen3.5-4B-Q4_K_M-GGUF](https://modelscope.cn/models/diodel/Qwen3.5-4B-Q4_K_M-GGUF/files)，是原版 [Qwen/Qwen3.5-4B](https://huggingface.co/Qwen/Qwen3.5-4B) 的 Q4_K_M 转换版本。两个来源都使用固定修订，具体文件名、大小与校验值见[第三方声明](code/THIRD-PARTY.md)。

模型会根据当前设备的可用显存自动调整计算缓冲区，并由 llama.cpp 自动分配模型层，小显存设备也能顺利加载；GPU 初始化失败时，鱼鱼会自动切换到 CPU 兼容模式，并在设置中显示实际的运行模式。

## 连接桌面 Agent（MCP）

从 1.0.0 起，标准版的蓝色大肥鱼提供一个标准 MCP 接口，可以连接一个桌面 AI Agent，并接受它的命令。连接之后，主人可以在 AI Agent 端自行配置相关的逻辑和能力，实现电脑操控、与云端大模型对话、自定义动作逻辑等各种自定义功能。许多小伙伴期待的玩法，现在都可以交给一个普通的桌面 AI Agent 来完成，鱼鱼也因此拥有了无穷的拓展可能。

连接方法很简单：在设置的“Agent 与 MCP”页面中打开“开启 MCP 接口”，阅读并确认弹出的权限说明，再点击“复制 MCP 连接配置”，把复制到的内容填进 Agent 的 MCP 服务器配置中即可。配置指向随包安装的桥接程序，形如：

```json
{
  "mcpServers": {
    "big-blue-fish": {
      "command": "C:\\Program Files\\蓝色大肥鱼\\BigBlueFish.Mcp.exe",
      "args": []
    }
  }
}
```

Linux 上的 `command` 为 `/opt/bigbluefish/BigBlueFish.Mcp`。桥接程序会连接到正在运行的鱼鱼，连接成功后，设置页面会显示当前的连接数量。

连接后，Agent 可以使用以下能力：

- 读取和修改鱼鱼的设置，查看她当前的动作、队列和模型状态；
- 获取当前前台窗口或整个屏幕的截图；
- 查询全部 106 个动作，让鱼鱼立即播放某个动作，或者把动作排进队列依次播放；
- 让鱼鱼立即说出一段指定的话，或者在下一次正常说话时机说出来；
- 实时接收主人点击、拖动鱼鱼的交互事件。

如果希望完全交给 Agent 来安排，可以开启“提线木偶模式”。这时鱼鱼会暂停自己的说话和动作切换，安静地循环播放“待机呼吸休闲”，等待 Agent 指定动作和文字；外部动作播放完后，她会回到待机。想恢复原来的陪伴时，关闭提线木偶模式即可。开启“双击与鱼鱼对话”后，只要有 Agent 已经连接，双击鱼鱼就会在她身边展开一个小输入框，主人写下的话会交给 Agent，Agent 再通过鱼鱼的气泡回复主人。

即使接入了 AI Agent，鱼鱼在这条连接中做的事情也始终很简单：向 Agent 传入 Agent 主动要求的截图、主人主动输入的文字，以及主人点击、拖动她的交互事件，再接收 Agent 发来的文字、动作和设置等控制。鱼鱼不会自主命令 Agent，电脑操控、云端对话等各种操作能力，实际都由 Agent 自己执行。

**风险提醒：** 通过 MCP 连接后，截屏会被传递给 MCP 彼端，其处理方式由彼端应用决定，可能导致主人的信息被上传或保存。MCP 的权限对整个接口统一生效，开启后每个已连接的 Agent 都可以使用上面列出的全部能力，不按 Agent 或工具分别授权。设置中的“感知当前应用”开关只控制内置模型自己读屏，Agent 通过 MCP 请求的截图不受这个开关影响。MCP 接口默认关闭，开启前软件会弹窗说明这些权限，请只连接你信任的 Agent。

完整的连接方式、工具参数、事件格式和接入建议见 [MCP 接入参考](docs/MCP.md)。

## 无MCP版

为了彻底消除 MCP 可能带来的隐私风险，蓝色大肥鱼为 Windows 和 Linux 额外提供了无MCP版。无MCP版在编译时就移除了 MCP 控制服务、桥接程序、双击输入框和相关设置页面，安装包中只保留鱼鱼自己的陪伴功能，应用内更新也会继续获取无MCP版。安装包文件名中带有 `Safe` 字样，下载方式与标准版相同。

## 从仓库部署

仓库面向开发者和维护者，包含源码、动画资源、地区数据、构建与发布脚本、测试程序、依赖锁文件和许可文件。模型与推理运行时由脚本另行准备。在 Windows x64 上安装 .NET 10 SDK 后，可以从仓库根目录构建：

```powershell
# 标准版
./code/build.ps1 -Locked -InputMode Ocr

# 无MCP版
dotnet build ./code/BigBlueFish.Windows/BigBlueFish.Windows.csproj -c Release -p:Platform=x64 -p:CompanionEdition=Safe -p:RestoreLockedMode=true
```

准备模型时，可以用模型脚本从任一来源下载，或导入本机已有的 GGUF 文件，然后指定 llama.cpp 运行时目录和模型目录启动；不准备模型时，用 `-NoModel` 以无模型模式启动：

```powershell
./tools/Setup-Model.ps1 -Source HuggingFace -DestinationDirectory "D:\BigBlueFish-local"
./tools/Setup-Model.ps1 -Source ModelScope -DestinationDirectory "D:\BigBlueFish-local"
./tools/Setup-Model.ps1 -Source Manual -ModelFile "D:\下载\我的模型.gguf" -DestinationDirectory "D:\BigBlueFish-local"

./code/run.ps1 -InputMode Ocr -RuntimeDirectory "推理运行时目录" -ModelsDirectory "D:\BigBlueFish-local\models"
./code/run.ps1 -InputMode Ocr -NoModel
```

生成安装包需要一个已经核对过的 llama.cpp 运行时目录（Windows 使用 b10809 的 `win-vulkan-x64` 版本）。Linux 安装包在 WSL 中的 Ubuntu／Debian 环境里构建，需要 gcc、pkg-config、libdbus-1-dev、dpkg-deb 和 FFmpeg：

```powershell
# Windows 安装器，-Edition Safe 生成无MCP版
./tools/Build-Installer.ps1 -Edition Standard -RuntimeDirectory "Windows运行时目录"

# Ubuntu／Debian x64 安装包，-Edition Safe 生成无MCP版
./tools/Build-LinuxPackage.ps1 -Distribution "WSL发行版名称" -RuntimeDirectory "Linux运行时目录" -Edition Standard
```

项目版本统一记录在根目录的 `Version.props`，程序、MCP 桥接程序、安装器和卸载器都从这里生成版本信息。源码结构、修改入口、测试方式和历史视觉版的构建参数见[开发说明](code/README.md)。

## 文档与反馈

版本变化记录在 [CHANGELOG.md](CHANGELOG.md) 中。当前版本完整的窗口、动作权重、模型输入、天气新闻、安装更新等行为见[功能规格](docs/AGENT_FUNCTIONAL_SPEC.md)，各已发布版本的行为见[版本规格目录](docs/specs/README.md)，方便其他开发者和 AI 接手维护。欢迎通过 GitHub Issues 提交问题、建议和使用反馈，也感谢来自 B 站的小伙伴们一直以来的关爱和建议。

## 开源许可与第三方权利信息

本项目以 [Apache License 2.0](LICENSE) 发布。鱼鱼的形象来自对 DeepSeek 的二创虚拟形象“蓝色大肥鱼”；项目使用的动画、模型、运行时、字体和数据分别来自以下项目，各自遵循其原有许可：

- 角色动画：106 段透明 WebM 动画来自 [PC2005-cloud/dsh-pet](https://github.com/PC2005-cloud/dsh-pet)，MIT 许可，保留原有颜色、透明度与 24 帧时序，动作的选择与触发逻辑为本项目定制；
- 本地模型：Qwen3.5-4B 及其衍生的 heretic 版本与 GGUF 转换，Apache-2.0 许可，模型文件由用户另行下载；
- 推理与运行环境：llama.cpp（MIT）、.NET 10 运行时（MIT）、Microsoft Visual C++ 运行库（Microsoft 再分发条款）；
- 动画解码：Windows 版使用 FFmpeg 9.0.1（GPL-3.0-or-later），作为独立进程运行；Linux 版使用系统的 FFmpeg；
- Linux 界面：Avalonia、MicroCom.Runtime、Tmds.DBus.Protocol、SkiaSharp、HarfBuzzSharp（MIT）；
- 文字识别：Windows 版使用系统提供的 Windows.Media.Ocr，Linux 版使用 Tesseract；
- 气泡字体：乐米小奶泡体，按其公布的免费商用条款内嵌；
- 天气数据：[Open-Meteo](https://open-meteo.com/)，CC BY 4.0；科技新闻：[IT之家 RSS](https://www.ithome.com/rss/)；
- 行政区划数据：[AreaCity-JsSpider-StatsGov](https://github.com/xiangyuecn/AreaCity-JsSpider-StatsGov)，MIT 许可，仅内嵌层级与近似中心坐标。

各组件的版本、修订、校验值和版权说明见[第三方声明](code/THIRD-PARTY.md)，完整许可文本见[许可文件目录](code/licenses)。
