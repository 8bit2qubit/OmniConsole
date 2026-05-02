# OmniConsole

> 🌐 **English** | [繁體中文](README.zh-TW.md)

<p align="center">
<img src="OmniConsole/Assets/SplashScreen.scale-200.png" alt="OmniConsole" style="height: 80px; object-fit: contain; display: block; margin: 0 auto;">
</p>

<p align="center">
  <img src="docs/images/app-settings.png" alt="OmniConsole Settings" height="350"><img src="docs/images/widget-omnicharm.png" alt="OmniCharm Widget" height="350"><img src="docs/images/app-about.png" alt="OmniConsole About" height="350">
</p>

<p align="center">
<a href="https://github.com/8bit2qubit/OmniConsole/releases/latest"><img src="https://img.shields.io/github/v/release/8bit2qubit/OmniConsole?style=flat-square&color=blue" alt="Latest Release"></a>
<a href="https://github.com/8bit2qubit/OmniConsole/releases"><img src="https://img.shields.io/github/downloads/8bit2qubit/OmniConsole/total" alt="Total Downloads"></a>
<a href="#"><img src="https://img.shields.io/badge/tech-C%23%20%26%20C%2B%2B%20%7C%20.NET%208%20%7C%20WinUI%203-blueviolet.svg?style=flat-square" alt="Tech"></a>
<a href="https://github.com/8bit2qubit/OmniConsole/blob/main/LICENSE"><img src="https://img.shields.io/github/license/8bit2qubit/OmniConsole" alt="License"></a>
</p>

A custom **WinUI 3 gaming platform launcher** designed to replace the default Windows 11 **Full Screen Experience (FSE) Home shell**, providing a seamless, console-like boot experience for gaming PCs and handhelds.

---

## 💡 What is OmniConsole?

OmniConsole serves as the Windows 11 Full Screen Experience (FSE) Home shell on your PC or handheld device (ROG Xbox Ally X, etc.), launching your chosen gaming platform automatically whenever FSE is activated. The default FSE Home only supports the Xbox App — OmniConsole removes this limitation, letting you choose from:

- **On boot**: With "Enter full screen experience on startup" enabled, your gaming platform launches automatically at boot.
- **During use**: Press the **Xbox button**, then select **"Home"** in Game Bar to launch your gaming platform, or **"Library"** to open OmniConsole Settings by default.

### How It Works

> Trigger (System boot / Xbox button → Game Bar "Home" or "Library" / Start Menu → OmniConsole)  
> → OmniConsole activates  
> → Already in FSE: Launches your chosen gaming platform → OmniConsole hides and exits  
> → Outside FSE: FSE entry dialog → Confirm → Re-launches in FSE → Launches your chosen gaming platform → OmniConsole hides and exits

---

## ✨ Features

- **Automatic Platform Launch** – Launches your configured gaming platform on activation.
- **Automatic FSE Entry** – When launched outside of FSE mode (e.g., from the Start Menu), OmniConsole automatically triggers the FSE entry dialog.
- **Multi-Platform Support** – Supports **Steam Big Picture**, **Xbox App**, **Epic Games Store**, **Armoury Crate SE**, and **Playnite Fullscreen**.
- **Custom Platform Support (Experimental)** – Supports adding your own platforms via Protocol URI, executable path, or Packaged App (MSIX / APPX / Bundle), with a card cover image. Launch arguments are available when using the executable path type.
- **Platform Import & Export** – Supports sharing custom platform configurations as JSON. Right-click or long-press a card to export; use the Import button to import shared configurations.
- **Gamepad-Compatible File Picker** – A custom-built file picker that replaces the system FileOpenPicker (which does not support gamepad input). Browse for executables and cover images entirely with a controller. A "Browse (Windows)" button is also available for users who prefer the legacy system file picker.
- **Card-Grid Settings UI** – Large icon cards designed for large-screen and handheld use, operable with mouse, touch, or Xbox controller.
- **Game Bar Integration** – Configures how Game Bar's **"Home"** and **"Library"** buttons behave: **"Home"** launches your gaming platform, **"Library"** opens OmniConsole Settings by default, or passes through directly to a platform like Xbox App.
- **Troubleshoot Page** – A dedicated page for emergency FSE recovery: terminates Game Bar and enters FSE directly, bypassing the FSE confirmation dialog.
- **Environment Snapshot** – An "About" page that captures your system, hardware, and OmniConsole health status, allowing you to copy a Markdown report with one click for easy bug reporting.
- **Gamepad Support** – Navigate with **D-Pad** or **Left Stick**, press **A** to confirm, **B** to exit, **LB/RB** to switch category tabs, **Y** to add a custom platform, **X** to edit, and **Menu (☰)** to set the focused platform as default and launch it immediately (in FSE mode).
- **Gamepad Mouse Mode** – Uses your gamepad as a mouse and keyboard. Three modes: **Off**, **Auto** (browsers, File Explorer, Steam, Epic Games Store), and **Force On** (all apps except an exclusion list). Two controller layouts: **OmniNav** and **Classic**, with adjustable cursor speed.
- **OmniCharm Widget** – A Game Bar widget for in-game quick access — open **Task View**, the **Xbox Library**, or the **Steam Overlay** in one tap. Also toggles **Gamepad Mouse Mode**, controller layout, cursor speed, and long-press ☰ for the **Steam In-Game Overlay**.
- **Gamepad Steam Shortcuts** – Gamepad **⧉** button support for Steam Big Picture mode: short press to open the **Steam Menu**, long press for the **Quick Access Menu**. Long press **☰** in-game to open the **Steam In-Game Overlay**.
- **Dedicated Settings Entry** – A separate "**OmniConsole Settings**" entry appears in All Apps, so you can change your default platform anytime.
- **Native FSE Integration** – Registers as a Windows 11 Full Screen Experience Home App through the official FSE API.
- **In-App Updates** – Automatically checks for the latest GitHub releases, with built-in downloading and installation available directly from the Advanced settings page.
- **Multilingual UI** – Supports English, Traditional Chinese (繁體中文), and Simplified Chinese (简体中文).

---

## ⚙️ Prerequisites

Before installing OmniConsole, you need to enable the Windows 11 Full Screen Experience feature:

- **Desktops, Laptops, Tablets & Handhelds without Native FSE**: Use [Xbox Full Screen Experience Tool](https://github.com/8bit2qubit/XboxFullScreenExperienceTool) to enable FSE first.
- **Native FSE Handheld Devices** (e.g., ROG Xbox Ally series): FSE is natively supported. Install OmniConsole directly.
- **Xbox Controller Required**: Game Bar, FSE, and all gamepad features require an Xbox-compatible (XInput) controller with an Xbox button.

---

## 🚀 Quick Start

### 1. Install OmniConsole

Download the latest release from the [**Releases Page**](https://github.com/8bit2qubit/OmniConsole/releases/latest).

**Option A: Install.bat (Recommended)**

1.  Extract the `OmniConsole_*_x64.zip` file and run `Install.bat`. It will enable Developer Mode, install the certificate, install any missing framework dependencies, and install both MSIX packages automatically.

**Option B: Manual Install**

1.  **[Critical]** Go to **Windows Settings → System → Advanced** and enable **Developer Mode**.
2.  **[Critical]** Double-click the `.cer` file → click **Install Certificate** → Store Location: **Local Machine** → **Place all certificates in the following store** → Browse → select **Trusted People** → Finish.
3.  *(Optional — only needed on fresh/offline systems; online systems fetch these automatically)* Double-click each file inside `Dependencies\` to install the bundled framework packages (skip any that report an equal or newer version already installed).
4.  Double-click `OmniConsole_*_x64.msix` to install the main app.
5.  Double-click `OmniConsole.PhantomLink_*_x64-widget.msix` to install the OmniCharm widget.

### 2. Configure Your Default Platform

OmniConsole will present the Settings UI on **first launch** or **after app updates**. You can also open it manually anytime from the Start Menu:

1.  Open **"OmniConsole Settings"** from the Start Menu (All Apps).
2.  Select your preferred gaming platform from the card grid using a **mouse**, **touch**, or **Xbox controller** (**D-Pad/Left Stick** to navigate in all four directions, **A** to confirm):
    - **Steam Big Picture**
    - **Xbox App**
    - **Epic Games Store**
    - **Armoury Crate SE**
    - **Playnite Fullscreen**

    Your selection is saved automatically. Press **B** on your controller or click/press **Exit** to finish.

### 3. [Critical] Set as FSE Home App

<p>
  <img src="docs/images/fse-settings.png" alt="Full Screen Experience Settings" height="221">
</p>

1.  Go to **Windows Settings → Gaming → Full Screen Experience**.
2.  Set "Choose home app" to **OmniConsole**.
3.  Enable **"Enter full screen experience on startup"**.

### 4. Done!

Your gaming platform now launches via any of these entry points:

- **Game Bar**: Press the **Xbox button**, then select **"Home"** to launch your gaming platform, or **"Library"** to open OmniConsole Settings by default.
- **Boot**: Enable **"Enter full screen experience on startup"** for automatic launch at boot.
- **Start Menu**: Launch OmniConsole directly to automatically activate the Full Screen Experience (FSE).

---

## 🔄 How to Revert

> ⚠️ **Change the FSE Home App setting _before_ uninstalling OmniConsole.** If OmniConsole is removed while it is still set as the FSE Home App, Windows **Task View will stop working** on some builds. This is a bug in Windows itself.

1. Go to **Windows Settings → Gaming → Full Screen Experience**.
2. Set "Choose home app" to **Xbox** or **None**.
3. Right-click **OmniConsole** in the Start Menu and select **Uninstall**, or go to **Windows Settings → Apps → Installed apps** to uninstall it.
4. Go to **Windows Settings → Apps → Installed apps** and uninstall **OmniCharm** (the widget does not appear in the Start Menu).

---

## 🛠️ Troubleshooting

If you experience an issue where the Windows Full Screen Experience (FSE) entry dialog ("Restart for better performance") fails to appear due to a Windows bug:

1. Open **OmniConsole Settings** from the Start Menu.
2. Navigate to the **Troubleshoot** tab using the left menu.
3. Click the **"Run"** button next to **"Terminate Game Bar & Enter FSE"**. This will force-close Game Bar and enter FSE directly, bypassing the FSE confirmation dialog.

---

## 💻 Tech Stack

- **Primary Stack**: C# & .NET 8, C++
- **UI Framework**: WinUI 3
- **Packaging**: MSIX

---

## 🛠️ Local Development

1.  **Clone the Repository**

    ```bash
    git clone https://github.com/8bit2qubit/OmniConsole.git
    cd OmniConsole
    ```

2.  **Open in Visual Studio**

    Open `OmniConsole.sln` with Visual Studio 2026 (18.0+). Ensure the **WinUI application development** workload is installed.

3.  **Run for Development**

    Set the build configuration to `Debug`, select your platform (`x64` / `ARM64`), and press `F5`.

---

## 🌟 Star History

<a href="https://star-history.com/#8bit2qubit/OmniConsole&Date">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/svg?repos=8bit2qubit/OmniConsole&type=Date&theme=dark" />
    <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/svg?repos=8bit2qubit/OmniConsole&type=Date" />
    <img alt="Star History Chart" src="https://api.star-history.com/svg?repos=8bit2qubit/OmniConsole&type=Date" />
  </picture>
</a>

---

## 📄 License

This project is licensed under the [GNU General Public License v3.0 (GPL-3.0)](https://github.com/8bit2qubit/OmniConsole/blob/main/LICENSE).

You are free to use, modify, and distribute this software, but any derivative works must also be distributed under the **same GPL-3.0 license and provide the complete source code**. For more details, see the [official GPL-3.0 terms](https://www.gnu.org/licenses/gpl-3.0.html).
