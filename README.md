# PptCollector

本機 PowerPoint 投影片搜尋與合併工具。適用於 macOS 和 Windows，搜尋母資料夾與子資料夾中的 `.pptx`。輸出是可編輯的 `.pptx`。

可從 GitHub Releases 下載 Apple Silicon Mac 版、Windows x64 安裝版及免安裝版。Mac 解壓縮後開啟 `PptCollector.app`。Windows 免安裝版解壓縮後開啟 `ppt-collector.exe`，並保留旁邊的 `engine` 資料夾。

1. 選擇簡報資料夾，輸入一個或多個關鍵字。
2. 選「任一符合」或「全部符合」，搜尋後勾選投影片。
3. 輸入檔名，按「匯出 .pptx」。輸出會存回原本選的母資料夾。同名檔請改名。

搜尋涵蓋投影片標題、文字方塊、群組文字和表格文字。舊版 `.ppt` 請先另存成 `.pptx`。若來源尺寸不同，先依尺寸勾選，再分開匯出。來源變更、樣式衝突或套件結構無法確認時，App 會顯示原因，讓你調整選取或重新搜尋。

## 建置

需要 Rust stable、Node.js 24 及 .NET SDK 8 或更新版。macOS 需要 Xcode Command Line Tools，Windows 需要 Microsoft C++ Build Tools 和 WebView2。請在要交付的作業系統上建置。

```sh
npm ci
npm run build
```

- macOS：開啟 `target/release/bundle/macos/PptCollector.app`。
- Windows：安裝 `target/release/bundle/nsis/` 下的 `-setup.exe`。

App 使用 Rust + Tauri 管理視窗、搜尋和匯出。`engine/` 是隨 App 打包的 Open XML 讀檔與合併元件，使用者不必另外安裝 .NET 或 PowerPoint 就能執行搜尋和匯出。請以 PowerPoint 檢查匯出檔。程式不需要連線即可處理簡報。

本專案採用 [MIT 授權](LICENSE)。
