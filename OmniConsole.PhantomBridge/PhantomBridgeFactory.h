#pragma once

#include "PhantomBridge.PhantomBridgeFactory.g.h"

// ============================================================================
// PhantomBridgeFactory 介面與 client watchdog 鉤子
// ============================================================================

// 由 OmniConsole.PhantomBridge.cpp 提供：第一次 RPC dispatch thread 內呼叫時記錄 client 行程
// 並掛上 RegisterWaitForSingleObject，client 死亡時自動 SetEvent 喚醒主執行緒退出。
void TryInstallClientWatchdog() noexcept;

namespace winrt::PhantomBridge::implementation
{
    struct PhantomBridgeFactory : PhantomBridgeFactoryT<PhantomBridgeFactory>
    {
        PhantomBridgeFactory() = default;

        void SendTaskView();
        void OpenSettings();
        void TriggerSteamInGameOverlay(winrt::hstring const& shortcut);
        void OpenXboxLibrary();
        void GetForegroundAppInfo(winrt::hstring& title, winrt::hstring& processName, winrt::hstring& fullPath, winrt::hstring& aumid, winrt::hstring& displayName, bool& isElevated);
        void OpenProfileEditor(winrt::hstring const& appId, winrt::hstring const& name, winrt::hstring const& fullPath);
    };
}

namespace winrt::PhantomBridge::factory_implementation
{
    struct PhantomBridgeFactory : PhantomBridgeFactoryT<PhantomBridgeFactory, implementation::PhantomBridgeFactory>
    {
    };
}
