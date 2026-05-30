# 驗證你的 OmniConsole 安裝

> 🌐 [English](AUTHENTICITY.md) | **繁體中文**

本文件說明如何確認你安裝的 OmniConsole 是來自本儲存庫的官方建置。

## 官方來源

OmniConsole 的官方來源僅有：

    https://github.com/8bit2qubit/OmniConsole
    https://8bit2qubit.github.io/omniconsole-site/download

任何其他來源（鏡像、fork、第三方網站）皆未經本專案背書，內容可能已被修改。

## 比對憑證指紋

開啟 OmniConsole 設定 → ☰ → 關於 → **發行資訊** → **詳細資訊**，將「**憑證詳細資訊**」對話方塊中顯示的 **憑證** SHA-256 指紋與下方公布值比對。

**官方 SHA-256 指紋：**

    DA:39:35:21:02:3B:87:EF:BF:52:95:CC:2D:AC:3D:DC:3A:75:7F:84:30:34:27:F8:9D:DB:59:EE:27:2A:5C:9A

若數值不一致，你安裝的建置便不是來自本儲存庫。

## 自行檢查憑證

你可以不依賴關於頁，自行用 PowerShell 驗證：

```powershell
Get-AppxPackage -Name b5fbce6b-2d7d-4da0-b419-4beb30e2b808 |
  ForEach-Object {
    $sig = Join-Path $_.InstallLocation 'AppxSignature.p7x'
    $hash = (Get-AuthenticodeSignature -FilePath $sig).
              SignerCertificate.GetCertHashString('SHA256')
    ($hash -split '(.{2})' -ne '') -join ':'
  }
```

輸出為冒號分隔的 SHA-256 指紋，格式與關於頁顯示一致。與上方官方值比對時不分大小寫。
