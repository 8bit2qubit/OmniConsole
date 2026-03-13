using System.Collections.Generic;
using System.Linq;

namespace OmniConsole.Models
{
    /// <summary>
    /// 集中管理所有支援遊戲平台的參數定義。
    ///
    /// 新增平台：只需在 <see cref="All"/> 清單末尾加入新的 <see cref="PlatformDefinition"/>，
    /// 無需修改任何其他程式碼（Service、ViewModel、UI 皆自動適配）。
    /// 修改啟動策略：直接調整對應平台的 LaunchStrategies 陣列。
    /// 啟動策略執行順序：依陣列順序逐一嘗試，第一個成功即停止。
    /// </summary>
    public static class PlatformCatalog
    {
        public static readonly IReadOnlyList<PlatformDefinition> All =
        [
            // ── Steam Big Picture ─────────────────────────────────────────────
            new()
            {
                Id = "SteamBigPicture",
                DisplayNameKey = "Platform_SteamBigPicture",
                IconAsset = "ms-appx:///Assets/Platforms/steam.png",
                AvailabilityStrategy = new()
                {
                    Type = LaunchStrategyType.ProtocolUri,
                    Uri = "steam://open/bigpicture",
                },
                LaunchStrategies =
                [
                    // 策略一：Protocol URI（Steam 已登錄 URI handler 時最快）
                    new()
                    {
                        Type = LaunchStrategyType.ProtocolUri,
                        Uri = "steam://open/bigpicture",
                    },
                    // 策略二：從 HKLM 登錄機碼讀取安裝路徑
                    new()
                    {
                        Type = LaunchStrategyType.Registry,
                        RegistryRoot = "HKLM",
                        RegistrySubKey = @"SOFTWARE\Valve\Steam",
                        RegistryValueName = "InstallPath",
                        ExecutableName = "steam.exe",
                        Arguments = "-bigpicture",
                    },
                ],
            },

            // ── Xbox App ──────────────────────────────────────────────────────
            new()
            {
                Id = "XboxApp",
                DisplayNameKey = "Platform_XboxApp",
                IconAsset = "ms-appx:///Assets/Platforms/xbox.png",
                HomeUri = "xbox://",
                LibraryUri = "xbox://library",
                AvailabilityStrategy = new()
                {
                    Type = LaunchStrategyType.ProtocolUri,
                    Uri = "xbox://",
                },
                LaunchStrategies =
                [
                    // 策略一：Protocol URI（最快，Xbox App 已登錄 URI handler）
                    new()
                    {
                        Type = LaunchStrategyType.ProtocolUri,
                        Uri = "xbox://",
                    },
                    // 策略二：封裝應用程式以 PackageName + Publisher 啟動
                    new()
                    {
                        Type = LaunchStrategyType.PackagedApp,
                        PackageName = "Microsoft.GamingApp",
                        Publisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US",
                    },
                    // 策略三：封裝應用程式以 PackageFamilyName 啟動
                    new()
                    {
                        Type = LaunchStrategyType.PackagedApp,
                        PackageFamilyName = "Microsoft.GamingApp_8wekyb3d8bbwe",
                    },
                ],
            },

            // ── Epic Games Launcher ───────────────────────────────────────────
            new()
            {
                Id = "EpicGames",
                DisplayNameKey = "Platform_EpicGames",
                IconAsset = "ms-appx:///Assets/Platforms/epic.png",
                AvailabilityStrategy = new()
                {
                    Type = LaunchStrategyType.ProtocolUri,
                    Uri = "com.epicgames.launcher://",
                },
                LaunchStrategies =
                [
                    // 策略一：Protocol URI
                    new()
                    {
                        Type = LaunchStrategyType.ProtocolUri,
                        Uri = "com.epicgames.launcher://",
                    },
                    // 策略二：從 HKCU 登錄機碼讀取 ModSdkCommand 取得安裝路徑
                    new()
                    {
                        Type = LaunchStrategyType.Registry,
                        RegistryRoot = "HKCU",
                        RegistrySubKey = @"SOFTWARE\Epic Games\EOS",
                        RegistryValueName = "ModSdkCommand",
                    },
                    // 策略三：直接執行 Epic Games Launcher
                    new()
                    {
                        Type = LaunchStrategyType.Executable,
                        ExecutableName = "EpicGamesLauncher.exe",
                        SearchPaths = [ @"%ProgramFiles(x86)%\Epic Games\Launcher\Portal\Binaries\Win64" ],
                    },
                ],
            },

            // ── Armoury Crate SE ─────────────────────────────────────────────
            new()
            {
                Id = "ArmouryCrateSE",
                DisplayNameKey = "Platform_ArmouryCrateSE",
                IconAsset = "ms-appx:///Assets/Platforms/armoury.png",
                AvailabilityStrategy = new()
                {
                    Type = LaunchStrategyType.ProtocolUri,
                    Uri = "asusac://",
                },
                LaunchStrategies =
                [
                    // 策略一：Protocol URI（輕量，優先嘗試）
                    new()
                    {
                        Type = LaunchStrategyType.ProtocolUri,
                        Uri = "asusac://",
                    },
                    // 策略二：封裝應用程式以 PackageName + Publisher 啟動
                    new()
                    {
                        Type = LaunchStrategyType.PackagedApp,
                        PackageName = "B9ECED6F.ArmouryCrateSE",
                        Publisher = "CN=38BC0208-0916-4E44-909B-E6832F47CDE7",
                    },
                    // 策略三：封裝應用程式以 PackageFamilyName 啟動
                    new()
                    {
                        Type = LaunchStrategyType.PackagedApp,
                        PackageFamilyName = "B9ECED6F.ArmouryCrateSE_qmba6cd70vzyy",
                    },
                ],
            },

            // ── Playnite Fullscreen ───────────────────────────────────────────
            new()
            {
                Id = "PlayniteFullscreen",
                DisplayNameKey = "Platform_PlayniteFullscreen",
                IconAsset = "ms-appx:///Assets/Platforms/playnite.png",
                AvailabilityStrategy = new()
                {
                    Type = LaunchStrategyType.ProtocolUri,
                    Uri = "playnite://",
                },
                LaunchStrategies =
                [
                    // 策略一：從 playnite:// URI handler 登錄機碼解析安裝目錄
                    // Playnite 允許自訂安裝路徑，此策略可應對任意安裝位置
                    new()
                    {
                        Type = LaunchStrategyType.Registry,
                        RegistryRoot = "HKCU",
                        RegistrySubKey = @"Software\Classes\playnite\shell\open\command",
                        RegistryValueName = "",
                        ParseCommandToDirectory = true,
                        ExecutableName = "Playnite.FullscreenApp.exe",
                        Arguments = "--hidesplashscreen",
                    },
                    // 策略二：預設安裝路徑備援
                    new()
                    {
                        Type = LaunchStrategyType.Executable,
                        ExecutableName = "Playnite.FullscreenApp.exe",
                        Arguments = "--hidesplashscreen",
                        SearchPaths = [ @"%LOCALAPPDATA%\Playnite" ],
                    },
                ],
            },

            // ── 在此新增更多平台 ──────────────────────────────────────────────
            // new()
            // {
            //     Id = "MyNewPlatform",
            //     DisplayNameKey = "Platform_MyNewPlatform",
            //     IconAsset = "ms-appx:///Assets/Platforms/myplatform.png",
            //     AvailabilityStrategy = new() { Type = LaunchStrategyType.ProtocolUri, Uri = "myplatform://" },
            //     LaunchStrategies = [ new() { Type = LaunchStrategyType.ProtocolUri, Uri = "myplatform://" } ],
            // },
        ];

        /// <summary>
        /// 以 Id 查找平台定義，找不到則傳回 null。
        /// </summary>
        public static PlatformDefinition? FindById(string id) =>
            All.FirstOrDefault(p => p.Id == id);
    }
}
