using System;
using System.Runtime.InteropServices;

namespace OmniConsole.PhantomLink.Services
{
    // ============================================================================
    // PhantomBridge Full Trust COM Server 的 C# 手動投影
    // ============================================================================
    //
    // PhantomBridgeFactory 註冊為 packaged COM ExeServer（com:ComServer），不是
    // activatable runtime class，winmd 自動投影在 Release（.NET Native AOT）會擲
    // REGDB_E_CLASSNOTREG。改以 CoCreateInstance(CLSCTX_LOCAL_SERVER, CLSID) +
    // [ComImport] 手動宣告 vtable，Debug/Release 行為一致。
    //
    // 變更 IDL 時：
    //   1. 同步下方 C# 介面方法宣告順序（vtable 順序需對齊）
    //   2. 重新建置 PhantomBridge → 重新建置 PhantomLink；csproj 的
    //      GeneratePhantomBridgeIIDs target 會呼叫 Build\GeneratePhantomBridgeIIDs.ps1，
    //      從 cppwinrt 產出的 PhantomBridge.0.h 解析 IID 並寫入 PhantomBridgeIIDs.g.cs
    // ============================================================================

    /// <summary>
    /// PhantomBridge Factory 的預設 WinRT 介面（手動投影，非 winmd 自動產出）。
    /// IID 取自建置時產生的 PhantomBridgeIIDs（見類別頂部說明）。
    ///
    /// 方法順序對齊 IDL（PhantomBridgeFactory.idl）：
    ///   SendTaskView → OpenSettings → TriggerSteamInGameOverlay → OpenXboxLibrary
    /// </summary>
    [ComImport]
    [Guid(PhantomBridgeIIDs.IPhantomBridgeFactory)]
    [InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
    internal interface IPhantomBridgeFactory
    {
        void SendTaskView();
        void OpenSettings();
        void TriggerSteamInGameOverlay([MarshalAs(UnmanagedType.HString)] string shortcut);
        void OpenXboxLibrary();
    }

    /// <summary>
    /// 透過 CoCreateInstance(CLSCTX_LOCAL_SERVER) 取得 PhantomBridge Full Trust COM Server 的 factory 實例。
    /// Windows 首次呼叫時自動啟動 OmniConsole.PhantomBridge.exe；client 結束後本 server 自動退出。
    /// Widget 於 UWP AppContainer 中，實測無法直接 SendInput / ShellExecute 自訂 protocol，
    /// 改委派給 fulltrust 桌面行程執行。
    /// </summary>
    internal static class PhantomBridgeHelper
    {
        // ── 常數 ─────────────────────────────────────────────────────────────

        /// <summary>OmniConsole.PhantomBridge.exe 註冊的 COM server CLSID（對齊 C++ PhantomBridgeFactoryClsid.h 與 Package.appxmanifest）。</summary>
        private static readonly Guid CLSID_PhantomBridgeFactory =
            new Guid("0370C27A-B39D-4B74-B20A-639B49026B14");

        /// <summary>IPhantomBridgeFactory 介面 IID（建置時從 PhantomBridge.0.h 自動產生）。</summary>
        private static readonly Guid IID_IPhantomBridgeFactory =
            new Guid(PhantomBridgeIIDs.IPhantomBridgeFactory);

        private const uint CLSCTX_LOCAL_SERVER = 0x4;

        // ── P/Invoke ─────────────────────────────────────────────────────────

        [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int CoCreateInstance(
            [In] ref Guid rclsid,
            IntPtr pUnkOuter,
            uint dwClsContext,
            [In] ref Guid riid,
            out IntPtr ppv);

        // ── 公開 API ─────────────────────────────────────────────────────────

        /// <summary>
        /// 取得 factory 實例。擲出例外（含 COM HRESULT）：CLASS_NOT_REG / SERVER_EXEC_FAILURE 等。
        /// 直接以 IPhantomBridgeFactory IID 請求（而非 IInspectable），省掉 .NET Native 的 QI 邏輯。
        /// </summary>
        public static IPhantomBridgeFactory CreateFactory()
        {
            Guid clsid = CLSID_PhantomBridgeFactory;
            Guid iid = IID_IPhantomBridgeFactory;
            IntPtr ptr;
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_LOCAL_SERVER, ref iid, out ptr);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);

            try
            {
                return (IPhantomBridgeFactory)Marshal.GetObjectForIUnknown(ptr);
            }
            finally
            {
                if (ptr != IntPtr.Zero) Marshal.Release(ptr);
            }
        }
    }
}
