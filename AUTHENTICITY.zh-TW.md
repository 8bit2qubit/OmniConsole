# 驗證你的 OmniConsole 安裝

> 🌐 [English](AUTHENTICITY.md) | **繁體中文**

本文件說明如何確認你安裝的 OmniConsole 是來自本儲存庫的官方建置。

## 官方來源

OmniConsole 的官方來源僅有：

    https://github.com/8bit2qubit/OmniConsole/releases
    https://8bit2qubit.github.io/omniconsole-site/download

任何其他來源（鏡像、fork、第三方網站）皆未經本專案背書，內容可能已被修改。

## 比對憑證指紋

開啟 OmniConsole 設定 → ☰ → 關於 → **發行資訊** → **詳細資訊**，將「**憑證詳細資訊**」對話方塊中顯示的 **憑證** SHA-256 指紋與下方公布值比對。

**官方 SHA-256 指紋：**

    DA:39:35:21:02:3B:87:EF:BF:52:95:CC:2D:AC:3D:DC:3A:75:7F:84:30:34:27:F8:9D:DB:59:EE:27:2A:5C:9A

若數值不一致，你安裝的建置便不是來自本儲存庫。

## 自行檢查憑證

你可以不依賴關於頁，自行用 PowerShell 驗證。完整的 OmniConsole 安裝包含兩個套件：**OmniConsole 主體**與 **OmniCharm 小工具**，兩者都應驗證。下列指令會一次檢查這兩個套件：

```powershell
$packages = @(
  'b5fbce6b-2d7d-4da0-b419-4beb30e2b808'  # OmniConsole
  '4fa8e044-7ffa-4059-b034-e4111881d96e'  # OmniCharm 小工具
)
$packages | ForEach-Object { Get-AppxPackage -Name $_ } | ForEach-Object {
  $sig  = Join-Path $_.InstallLocation 'AppxSignature.p7x'
  $hash = (Get-AuthenticodeSignature -FilePath $sig).SignerCertificate.GetCertHashString('SHA256')
  '{0}: {1}' -f $_.Name, (($hash -split '(.{2})' -ne '') -join ':')
}
```

兩個套件由同一張憑證簽署，每行開頭為套件識別碼，指紋皆應等於上方官方值（不分大小寫）。任一指紋不符，該套件便不是來自本儲存庫。若只顯示 OmniConsole 那一行，表示尚未安裝 OmniCharm 小工具。
