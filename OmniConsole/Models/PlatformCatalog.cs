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
                    // 策略二：從 HKCU 登錄機碼讀取安裝路徑
                    new()
                    {
                        Type = LaunchStrategyType.Registry,
                        RegistryRoot = "HKCU",
                        RegistrySubKey = @"SOFTWARE\Valve\Steam",
                        RegistryValueName = "InstallPath",
                        ExecutableName = "steam.exe",
                        Arguments = "-bigpicture",
                    },
                    // 策略三：從 HKLM 讀取（部分安裝情境的備援）
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
                    new()
                    {
                        Type = LaunchStrategyType.ProtocolUri,
                        Uri = "xbox://",
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
                    new()
                    {
                        Type = LaunchStrategyType.ProtocolUri,
                        Uri = "com.epicgames.launcher://",
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
                    // 策略二：MSIX 應用程式啟動（填入 PackageName 與 Publisher）
                    new()
                    {
                        Type = LaunchStrategyType.MsixPackage,
                        PackageName = "B9ECED6F.ArmouryCrateSE",
                        Publisher = "CN=38BC0208-0916-4E44-909B-E6832F47CDE7",
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
                    // 執行 Playnite 全螢幕應用程式
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
