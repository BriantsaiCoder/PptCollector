# PptCollector

本機 PowerPoint 投影片搜尋與合併工具。適用於 macOS 和 Windows，搜尋母資料夾與子資料夾中的 `.pptx`。輸出是可編輯的 `.pptx`。

可從 GitHub Releases 下載 Apple Silicon Mac 版、Windows x64 安裝版及免安裝版。Mac 解壓縮後開啟 `PptCollector.app`。Windows 免安裝版請完整解壓縮到同一個資料夾，再開啟最外層的 `PptCollector.exe`。

1. 選擇簡報資料夾，輸入一個或多個關鍵字。
2. 選「任一符合」或「全部符合」，搜尋後勾選投影片。點擊來源檔名可開啟原檔。對任一結果列按右鍵，可選「開啟檔案」或「開啟所在資料夾」。鍵盤焦點在檔名上時，也可按 Shift+F10 開啟選單。
3. 輸入檔名，可勾選附加 `_(Security C)` 作為檔名標記。按「匯出 .pptx」，所有勾選的投影片會合併到一份檔案，存回原本選的母資料夾。遇到同名檔會自動附加本機日期與時間，機密標記仍放在 `.pptx` 前。完成後底部顯示實際儲存路徑。

搜尋涵蓋投影片標題、文字方塊、群組文字和表格文字。舊版 `.ppt` 請先另存成 `.pptx`。合併保留來源投影片的母片和主題，略過跳到其他投影片的連結，保留一般網址連結。

輸出畫布以匯出順序的第一張投影片為準。其他尺寸的物件會等比例縮放並置中，圖表或背景外觀可能有差異，動畫路徑不會隨畫布調整。備忘稿內容會保留並可編輯，各頁會共用第一份含備忘稿的來源版型。備忘稿版型或尺寸不同時，匯出後會顯示提示。來源檔案變更、內嵌字型衝突或套件結構無法確認時，App 會顯示原因。

## 版本與更新

開啟「版本與更新」可查看目前版號，並手動檢查有沒有新版。選擇「更新並重新開啟」前，可以先看從目前版本到新版的變更。重新開啟後需重新選擇資料夾和搜尋，已匯出的簡報保留在原處。

Windows 10／11 x64 免安裝版可在有寫入權限的本機固定 NTFS 磁碟上自動更新。其他位置可從更新視窗下載新版。新版主程式和簡報處理元件都啟動成功後才切換。若試跑失敗，原版本會重新開啟。若使用的免安裝版尚未提供「版本與更新」，請先手動下載新版的完整 ZIP 並解壓縮。

## 建置

需要 Rust stable、Node.js 24 及 .NET SDK 8 或更新版。macOS 需要 Xcode Command Line Tools，Windows 需要 Microsoft C++ Build Tools 和 WebView2。請在要交付的作業系統上建置。

```sh
npm ci
npm run build
```

- macOS：開啟 `target/release/bundle/macos/PptCollector.app`。
- Windows：安裝 `target/release/bundle/nsis/` 下的 `-setup.exe`。

App 使用 Rust + Tauri 管理視窗、搜尋和匯出。`engine/` 是隨 App 打包的 Open XML 讀檔與合併元件，使用者不必另外安裝 .NET 或 PowerPoint 就能執行搜尋和匯出。請以 PowerPoint 檢查匯出檔。程式不需要連線即可處理簡報。

### 測試更新套件

預設建置不啟用線上更新。用 Tauri 建置覆寫設定指定版號、固定 HTTPS 測試清單網址和測試公鑰（`plugins.updater.endpoints`、`plugins.updater.pubkey`）。每個更新檔必須用對應金鑰及該檔案內嵌的版號簽章。Windows 安裝版和 macOS 可用 Tauri 的 `bundle.createUpdaterArtifacts: true` 產生更新檔與簽章。啟用 macOS 發行更新前，須完成簽署、公證和實際升級驗證。

Windows x64 建置後用 Python 3 執行 `python scripts/package-portable.py --version <建置版號> --out artifacts`。用 `npm exec tauri -- signer sign --private-key-path <測試金鑰檔> --app-version <建置版號> <檔案路徑>` 簽署 `-portable-update.zip`。將完整的 `-portable.zip` 放在同一網址目錄，供手動下載。

清單採 Tauri 靜態格式，包含 `version`、`notes` 和 `platforms`。支援平台鍵為 `windows-x86_64-nsis`（`-setup.exe`）、`windows-x86_64-portable`（`-portable-update.zip`）及 `darwin-aarch64`（`.app.tar.gz`），各自帶入 `url` 和 `.sig` 檔案的 `signature` 文字。另加 `changes` 陣列，每筆含 `version` 和純文字 `notes`，保留先前發行紀錄供跨版本更新顯示。測試與正式環境各用自己的網址與金鑰。

更新套件出問題時，先讓該發行清單回應 HTTP 204，並保留上一版的完整下載。對已完成更新的使用者，可發布版號更高的修正版，或提供上一版的手動安裝檔／完整 ZIP。Windows 免安裝版的舊資料夾可供復原，請保留其中使用者自己存放的檔案。

本專案採用 [MIT 授權](LICENSE)。
