# OmniConsole

> 🌐 [English](README.md) | **繁體中文**

<p align="center">
<img src="OmniConsole/Assets/Square150x150Logo.scale-200.png" alt="OmniConsole 圖示" style="width: 150px; object-fit: contain; display: block; margin: 0 auto;">
</p>

<p align="center">
<a href="https://github.com/8bit2qubit/OmniConsole/releases/latest"><img src="https://img.shields.io/github/v/release/8bit2qubit/OmniConsole?style=flat-square&color=blue" alt="最新版本"></a>
<a href="https://github.com/8bit2qubit/OmniConsole/releases"><img src="https://img.shields.io/github/downloads/8bit2qubit/OmniConsole/total" alt="總下載次數"></a>
<a href="#"><img src="https://img.shields.io/badge/tech-C%23%20%26%20.NET%208%20%7C%20WinUI%203-blueviolet.svg?style=flat-square" alt="技術堆疊"></a>
<a href="https://github.com/8bit2qubit/OmniConsole/blob/main/LICENSE"><img src="https://img.shields.io/github/license/8bit2qubit/OmniConsole" alt="授權"></a>
</p>

一個自訂的 **WinUI 3 遊戲平台啟動器**，設計用來取代 Windows 11 預設的**全螢幕體驗 (FSE) 首頁 Shell**，為遊戲 PC 和掌機提供無縫的主機風格開機體驗。

---

## 💡 什麼是 OmniConsole？

OmniConsole 將您的 Windows PC 或掌機裝置（ROG Xbox Ally 等）變成主機般的體驗。系統預設的全螢幕體驗首頁僅支援啟動 Xbox App，而 OmniConsole 突破了這項限制，讓您自由選擇要啟動的遊戲平台：

- **開機時**：啟用「啟動時進入全螢幕體驗」後，開機即自動啟動您設定的遊戲平台。
- **使用中**：按下 **Xbox 鍵** 開啟 Game Bar，點選**首頁**或**媒體櫃**即可啟動遊戲平台。

### 運作方式

```
開機 / Game Bar 首頁・媒體櫃 → OmniConsole 啟動 → 啟動您選擇的平台 → 自動隱藏
```

此應用程式透過 Windows 11 全螢幕體驗 API 註冊為首頁應用程式。

---

## ✨ 功能特色

- **自動平台啟動** – 啟動時自動開啟已設定的遊戲平台。
- **多平台支援** – 支援 **Steam Big Picture**、**Xbox App** 和 **Epic Games Launcher**。
- **專屬設定入口** – 在「所有應用程式」中獨立顯示「**OmniConsole 設定**」，隨時可更改預設平台，不影響 FSE 行為。
- **原生 FSE 整合** – 透過 Windows 11 全螢幕體驗 API 註冊為首頁應用程式。

---

## ⚙️ 前置條件

在安裝 OmniConsole 之前，您需要先啟用 Windows 11 的全螢幕體驗功能：

- **桌上型電腦 / 筆記型電腦**：請先使用 [Xbox Full Screen Experience Tool](https://github.com/8bit2qubit/XboxFullScreenExperienceTool) 啟用 FSE 功能。
- **原生掌機裝置**（如 ROG Xbox Ally 系列）：這些裝置已原生支援 FSE，無需使用 Xbox Full Screen Experience Tool，可直接安裝 OmniConsole。

---

## 🚀 快速入門

### 1. 安裝 OmniConsole

1.  從[**發布頁面**](https://github.com/8bit2qubit/OmniConsole/releases/latest)下載最新的 `.msix` 安裝包。
2.  將自簽憑證安裝到您的**受信任的人**憑證存放區（隨發布包附帶）。
3.  點兩下 `.msix` 檔案進行安裝。

### 2. 設定預設平台

1.  從開始功能表（所有應用程式）中開啟「**OmniConsole 設定**」。
2.  選擇您偏好的遊戲平台：
    - **Steam Big Picture**
    - **Xbox App**
    - **Epic Games Launcher**
3.  點選**儲存設定**。

### 3. 設為 FSE 首頁應用程式

1.  前往**設定 → 遊戲 → 全螢幕體驗**。
2.  將「選擇首頁應用程式」設為 **OmniConsole**。
3.  啟用「**啟動時進入全螢幕體驗**」（選用）。

### 4. 完成！

按下 **Xbox 鍵** — OmniConsole 會即時啟動您選擇的平台並自動隱藏。

---

## 💻 技術堆疊

- **主要堆疊**：C# & .NET 8
- **UI 框架**：WinUI 3
- **封裝**：MSIX

---

## 🛠️ 本機開發

1.  **Clone 儲存庫**

    ```bash
    git clone https://github.com/8bit2qubit/OmniConsole.git
    cd OmniConsole
    ```

2.  **以 Visual Studio 開啟**

    使用 Visual Studio 2022 (17.0+) 開啟 `OmniConsole.sln`。確保已安裝 **WinUI 應用程式開發** 工作負載。

3.  **開發模式執行**

    將組建設定設為 `Debug`，選擇平台（`x64` / `ARM64`），按 `F5` 建置並執行。

---

## 📄 授權

本專案採用 [GNU 通用公共授權條款第 3 版 (GPL-3.0)](https://github.com/8bit2qubit/OmniConsole/blob/main/LICENSE) 授權。

您可以自由使用、修改和散佈本軟體，但任何衍生作品必須以**相同的 GPL-3.0 授權條款散佈並提供完整原始碼**。詳情請參閱 [GPL-3.0 官方條款](https://www.gnu.org/licenses/gpl-3.0.html)。
