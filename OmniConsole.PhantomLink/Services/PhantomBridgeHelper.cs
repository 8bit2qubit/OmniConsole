using System;
using System.Runtime.InteropServices;

namespace OmniConsole.PhantomLink.Services
{
    /// <summary>
    /// PhantomBridge Factory 的預設 WinRT 介面（手動投影，非 winmd 自動產出）。
    /// IID 由 Build\GeneratePhantomBridgeIIDs.ps1 從 PhantomBridge.0.h 解析後寫入 PhantomBridgeIIDs.g.cs。
    /// 方法宣告順序需與 IDL（PhantomBridgeFactory.idl）的 vtable 順序逐位相符：
    ///   SendTaskView → OpenSettings → TriggerSteamInGameOverlay → OpenXboxLibrary
    ///   → GetForegroundAppInfo → OpenProfileEditor
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
        void GetForegroundAppInfo(
            [Out, MarshalAs(UnmanagedType.HString)] out string title,
            [Out, MarshalAs(UnmanagedType.HString)] out string processName,
            [Out, MarshalAs(UnmanagedType.HString)] out string fullPath,
            [Out, MarshalAs(UnmanagedType.HString)] out string aumid,
            [Out, MarshalAs(UnmanagedType.HString)] out string displayName,
            [Out] out bool isElevated);
        void OpenProfileEditor(
            [MarshalAs(UnmanagedType.HString)] string appId,
            [MarshalAs(UnmanagedType.HString)] string name,
            [MarshalAs(UnmanagedType.HString)] string fullPath);
    }

    /// <summary>
    /// 透過 CoCreateInstance(CLSCTX_LOCAL_SERVER) 取得 PhantomBridge Full Trust COM Server 的 factory 實例。
    /// Windows 首次呼叫時自動啟動 OmniConsole.PhantomBridge.exe；呼叫端結束後本 server 自動退出。
    /// Widget 於 UWP AppContainer 中無法直接 SendInput / ShellExecute 自訂 protocol，
    /// 改委派給 full trust 桌面行程執行。
    /// </summary>
    internal static class PhantomBridgeHelper
    {
        // ── 常數 ─────────────────────────────────────────────────────────────

        /// <summary>OmniConsole.PhantomBridge.exe 註冊的 COM server CLSID（與 C++ PhantomBridgeFactoryClsid.h、Package.appxmanifest 同值）。</summary>
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
        /// 直接以 IPhantomBridgeFactory IID 請求（而非 IInspectable）。
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
