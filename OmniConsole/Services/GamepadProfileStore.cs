using OmniConsole.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.Storage;

namespace OmniConsole.Services
{
    /// <summary>
    /// 玩家自訂 per-App 手把映射 profile 的持久化。
    /// 檔案位置：PublisherCacheFolder\OmniConsoleShared\GamepadProfiles.json
    /// （與 Shared.ini 同目錄；PhantomKey C++ 端讀，OmniConsole C# 端讀寫）。
    /// </summary>
    public static class GamepadProfileStore
    {
        private const string SharedFolderName = "OmniConsoleShared";
        private const string ProfilesFileName = "GamepadProfiles.json";
        private const int SchemaVersion = 1;

        private static string? _cachedPath;

        // ── 路徑解析 ──

        /// <summary>取 PublisherCacheFolder 下的 profile 檔完整路徑；首次取得後快取，取不到時回空字串。</summary>
        private static string ProfilesPath
        {
            get
            {
                if (_cachedPath != null) return _cachedPath;
                try
                {
                    var folder = ApplicationData.Current.GetPublisherCacheFolder(SharedFolderName);
                    _cachedPath = Path.Combine(folder.Path, ProfilesFileName);
                }
                catch
                {
                    _cachedPath = string.Empty;
                }
                return _cachedPath;
            }
        }

        // ── 硬性黑名單 ─────────────────────────────────────────────────────

        /// <summary>
        /// 判定 appId 是否屬於不開放自訂 profile 的集合。
        /// Process： 與 s_blacklistedProcesses 或 s_mouseModeAutoTargets 任一命中即返回 true。
        /// AUMID： 對整段 AUMID 與 s_blacklistedPfnSubstrings 逐項做子字串比對，任一命中即返回 true。
        /// </summary>
        public static bool IsBlacklisted(AppId appId)
        {
            if (appId == null || string.IsNullOrEmpty(appId.Value)) return false;
            if (appId.Kind == IdKind.Process)
            {
                if (s_blacklistedProcesses.Contains(appId.Value)) return true;
                if (s_mouseModeAutoTargets.Contains(appId.Value)) return true;
                return false;
            }
            string v = appId.Value;
            foreach (var sub in s_blacklistedPfnSubstrings)
            {
                if (v.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>process 名比對集合（大小寫不敏感）。</summary>
        private static readonly HashSet<string> s_blacklistedProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "OmniConsole",
            "Playnite.FullscreenApp",
            "steamwebhelper",        // Steam Big Picture / 桌面 Steam 共用此 exe
        };

        /// <summary>Mouse Mode Auto 涵蓋的程式（內建 OmniNav / Classic 已套用對應配置）。</summary>
        private static readonly HashSet<string> s_mouseModeAutoTargets = new(StringComparer.OrdinalIgnoreCase)
        {
            "msedge", "chrome", "firefox", "opera", "brave",
            "EpicGamesLauncher", "Discord",
            "explorer",
        };

        /// <summary>AUMID 形如 <PFN>!<AppId>，對 AUMID 整段做子字串搜尋，搜尋的目標子字串實質為 PFN。</summary>
        private static readonly string[] s_blacklistedPfnSubstrings =
        {
            "Microsoft.GamingApp",                      // Xbox App
            "B9ECED6F.ArmouryCrateSE",                  // Armoury Crate SE
            "windows.immersivecontrolpanel",            // Windows 設定 (SystemSettings.exe ， packaged 但自跑 exe)
            "Microsoft.WindowsStore",                   // Microsoft Store (WinStore.App.exe ， packaged 但自跑 exe)
            "b5fbce6b-2d7d-4da0-b419-4beb30e2b808",     // OmniConsole 主程式自己（packaged）
        };

        // ── 讀 ─────────────────────────────────────────────────────────────

        /// <summary>讀取所有 profile；檔案不存在 / 解析失敗回空 list。</summary>
        public static List<GamepadProfile> Load()
        {
            var result = new List<GamepadProfile>();
            var path = ProfilesPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return result;

            try
            {
                string text = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(text)) return result;

                var node = JsonNode.Parse(text);
                if (node is not JsonObject root) return result;
                if (root["profiles"] is not JsonArray arr) return result;

                foreach (var item in arr)
                {
                    if (item is not JsonObject obj) continue;
                    var prof = ParseProfile(obj);
                    if (prof != null) result.Add(prof);
                }
            }
            catch
            {
                // 損毀檔當作空
            }
            return result;
        }

        /// <summary>
        /// 依 appId 找 profile；未命中回 null。
        /// 比對寬鬆：path-bound 候選找不到精確同 path 的 store 項時，回退到同 process 名的舊 name-only profile。
        /// </summary>
        public static GamepadProfile? Find(AppId appId)
        {
            if (appId == null) return null;
            var list = Load();
            foreach (var p in list)
                if (SameTarget(p.AppId, appId)) return p;
            // Process 類 + input 有 path → 寬鬆回退：尋找同 Value 但 store FullPath 為 null 的舊 name-only profile
            if (appId.Kind == IdKind.Process && !string.IsNullOrEmpty(appId.FullPath))
            {
                foreach (var p in list)
                {
                    if (p.AppId.Kind != IdKind.Process) continue;
                    if (!p.AppId.Matches(appId)) continue;
                    if (string.IsNullOrEmpty(p.AppId.FullPath)) return p;
                }
            }
            return null;
        }

        /// <summary>
        /// 判定兩個 AppId 是否指向同一個 profile 槽（精確比對）。
        /// Aumid 類：Kind 相同 + Value 相同。
        /// Process 類：Kind 相同 + Value 相同 + FullPath 正規化後相同（含兩邊都 null 也視為相同）。
        /// </summary>
        private static bool SameTarget(AppId a, AppId b)
        {
            if (a == null || b == null) return false;
            if (!a.Matches(b)) return false;  // Kind + Value 先過
            if (a.Kind == IdKind.Aumid) return true;
            string? pa = AppId.NormalizePath(a.FullPath);
            string? pb = AppId.NormalizePath(b.FullPath);
            return string.Equals(pa, pb, StringComparison.Ordinal);
        }

        /// <summary>
        /// Upsert 用的槽位比對：在 SameTarget 基礎上再放寬一條 — 同 Value 且 store 項 FullPath 為 null 的舊 name-only 視為同槽。
        /// </summary>
        private static bool SameSlot(AppId stored, AppId incoming)
        {
            if (stored == null || incoming == null) return false;
            if (SameTarget(stored, incoming)) return true;
            if (stored.Kind != IdKind.Process || incoming.Kind != IdKind.Process) return false;
            if (!stored.Matches(incoming)) return false;
            return string.IsNullOrEmpty(stored.FullPath) && !string.IsNullOrEmpty(incoming.FullPath);
        }

        // ── 寫 ─────────────────────────────────────────────────────────────

        /// <summary>覆寫整個 profile 集合（依現有檔重排）。PublisherCacheFolder 取不到時回 false。</summary>
        public static bool SaveAll(IEnumerable<GamepadProfile> profiles)
        {
            var path = ProfilesPath;
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                var root = new JsonObject
                {
                    ["version"] = SchemaVersion,
                    ["profiles"] = new JsonArray(profiles.Where(p => p != null).Select(SerializeProfile).ToArray())
                };
                var opts = new JsonSerializerOptions { WriteIndented = true };
                string text = root.ToJsonString(opts);
                File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>新增或覆寫一個 profile（黑名單擋回 false，不影響檔案）。</summary>
        public static bool Upsert(GamepadProfile profile)
        {
            if (profile == null || profile.AppId == null) return false;
            if (IsBlacklisted(profile.AppId)) return false;
            var list = Load();
            int idx = list.FindIndex(p => SameSlot(p.AppId, profile.AppId));
            if (idx >= 0) list[idx] = profile;
            else list.Add(profile);
            return SaveAll(list);
        }

        /// <summary>依 appId 刪除一個 profile。不存在時視為成功（true）。</summary>
        public static bool Delete(AppId appId)
        {
            if (appId == null) return false;
            var list = Load();
            int removed = list.RemoveAll(p => SameTarget(p.AppId, appId));
            if (removed == 0) return true;
            return SaveAll(list);
        }

        // ── 序列化 / 反序列化 ───────────────────────────────────────────────

        /// <summary>由 JSON 物件還原一個 GamepadProfile；appId.value 為空時回 null。</summary>
        private static GamepadProfile? ParseProfile(JsonObject obj)
        {
            var prof = new GamepadProfile();

            if (obj["appId"] is not JsonObject appIdObj) return null;
            string kindStr = appIdObj["kind"]?.GetValue<string>() ?? "process";
            string value = appIdObj["value"]?.GetValue<string>() ?? string.Empty;
            if (string.IsNullOrEmpty(value)) return null;
            string? fullPath = appIdObj["fullPath"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(fullPath)) fullPath = null;
            if (!AppId.IsValidFullPath(fullPath)) fullPath = null;
            prof.AppId = new AppId
            {
                Kind = string.Equals(kindStr, "aumid", StringComparison.OrdinalIgnoreCase)
                            ? IdKind.Aumid : IdKind.Process,
                Value = value,
                FullPath = fullPath
            };

            prof.DisplayName = obj["displayName"]?.GetValue<string>() ?? string.Empty;

            if (obj["bindings"] is JsonObject bindings)
            {
                foreach (var kv in bindings)
                {
                    if (kv.Value is not JsonObject actionObj) continue;
                    if (!Enum.TryParse<GamepadInputId>(kv.Key, ignoreCase: true, out var id)) continue;
                    var act = ParseAction(actionObj);
                    if (act != null) prof.Bindings[id] = act;
                }
            }
            return prof;
        }

        /// <summary>由 JSON 物件還原一個 GamepadAction；kind 無法解析時回 Kind=None 的 action。</summary>
        private static GamepadAction? ParseAction(JsonObject obj)
        {
            string kindStr = obj["kind"]?.GetValue<string>() ?? string.Empty;
            if (!Enum.TryParse<GamepadActionKind>(kindStr, ignoreCase: true, out var kind))
                return new GamepadAction { Kind = GamepadActionKind.None };
            var act = new GamepadAction { Kind = kind };
            switch (kind)
            {
                case GamepadActionKind.KeyTap:
                case GamepadActionKind.KeyHold:
                    act.Vk = obj["vk"]?.GetValue<int>() ?? 0;
                    break;
                case GamepadActionKind.KeyCombo:
                    act.Vk = obj["vk"]?.GetValue<int>() ?? 0;
                    if (obj["mods"] is JsonArray modsArr)
                    {
                        foreach (var m in modsArr)
                        {
                            string ms = m?.GetValue<string>() ?? string.Empty;
                            if (string.Equals(ms, "Ctrl", StringComparison.OrdinalIgnoreCase)) act.Mods |= GamepadModifier.Ctrl;
                            if (string.Equals(ms, "Shift", StringComparison.OrdinalIgnoreCase)) act.Mods |= GamepadModifier.Shift;
                            if (string.Equals(ms, "Alt", StringComparison.OrdinalIgnoreCase)) act.Mods |= GamepadModifier.Alt;
                            if (string.Equals(ms, "Win", StringComparison.OrdinalIgnoreCase)) act.Mods |= GamepadModifier.Win;
                        }
                    }
                    break;
                case GamepadActionKind.MouseButton:
                    string which = obj["which"]?.GetValue<string>() ?? "Left";
                    if (string.Equals(which, "Right", StringComparison.OrdinalIgnoreCase)) act.Which = GamepadMouseWhich.Right;
                    else if (string.Equals(which, "Middle", StringComparison.OrdinalIgnoreCase)) act.Which = GamepadMouseWhich.Middle;
                    else act.Which = GamepadMouseWhich.Left;
                    break;
                case GamepadActionKind.MouseWheel:
                    string dir = obj["dir"]?.GetValue<string>() ?? "Up";
                    if (string.Equals(dir, "Down", StringComparison.OrdinalIgnoreCase)) act.Dir = GamepadWheelDir.Down;
                    else if (string.Equals(dir, "Left", StringComparison.OrdinalIgnoreCase)) act.Dir = GamepadWheelDir.Left;
                    else if (string.Equals(dir, "Right", StringComparison.OrdinalIgnoreCase)) act.Dir = GamepadWheelDir.Right;
                    else act.Dir = GamepadWheelDir.Up;
                    break;
                default:
                    break;
            }
            return act;
        }

        /// <summary>將 GamepadProfile 寫成 JSON 物件；Kind=None 的 binding 不輸出。</summary>
        private static JsonObject SerializeProfile(GamepadProfile prof)
        {
            var bindings = new JsonObject();
            foreach (var kv in prof.Bindings)
            {
                if (kv.Value == null) continue;
                if (kv.Value.Kind == GamepadActionKind.None) continue;
                bindings[kv.Key.ToString()] = SerializeAction(kv.Value);
            }

            var appIdObj = new JsonObject
            {
                ["kind"] = prof.AppId.Kind == IdKind.Aumid ? "aumid" : "process",
                ["value"] = prof.AppId.Value ?? string.Empty
            };
            // FullPath 為 null 時不寫該 key
            if (!string.IsNullOrEmpty(prof.AppId.FullPath))
                appIdObj["fullPath"] = prof.AppId.FullPath;

            return new JsonObject
            {
                ["appId"] = appIdObj,
                ["displayName"] = prof.DisplayName ?? string.Empty,
                ["bindings"] = bindings
            };
        }

        /// <summary>將 GamepadAction 寫成 JSON 物件，依 kind 輸出對應欄位（vk / mods / which / dir）。</summary>
        private static JsonObject SerializeAction(GamepadAction a)
        {
            var obj = new JsonObject { ["kind"] = a.Kind.ToString() };
            switch (a.Kind)
            {
                case GamepadActionKind.KeyTap:
                case GamepadActionKind.KeyHold:
                    obj["vk"] = a.Vk;
                    break;
                case GamepadActionKind.KeyCombo:
                    obj["vk"] = a.Vk;
                    var mods = new JsonArray();
                    if ((a.Mods & GamepadModifier.Ctrl) != 0) mods.Add((JsonNode)JsonValue.Create("Ctrl"));
                    if ((a.Mods & GamepadModifier.Shift) != 0) mods.Add((JsonNode)JsonValue.Create("Shift"));
                    if ((a.Mods & GamepadModifier.Alt) != 0) mods.Add((JsonNode)JsonValue.Create("Alt"));
                    if ((a.Mods & GamepadModifier.Win) != 0) mods.Add((JsonNode)JsonValue.Create("Win"));
                    obj["mods"] = mods;
                    break;
                case GamepadActionKind.MouseButton:
                    obj["which"] = a.Which.ToString();
                    break;
                case GamepadActionKind.MouseWheel:
                    obj["dir"] = a.Dir.ToString();
                    break;
                default:
                    break;
            }
            return obj;
        }
    }
}
