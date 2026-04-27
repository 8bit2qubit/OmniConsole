#pragma once

#include <unknwn.h>
#include <thread>

// ============================================================================
// 自訂 module_lock
// ============================================================================
//
// counter 歸零時 SetEvent 通知主執行緒退出，主執行緒走 kernel wait 不耗 CPU。
// 必須在任何 winrt header include 之前 define + 宣告 get_module_lock，
// 否則 base.h 內的 module_lock_updater 模板實例化時 unqualified lookup 找不到。
#define WINRT_CUSTOM_MODULE_LOCK

namespace winrt
{
    struct module_lock
    {
        uint32_t operator++() noexcept;
        uint32_t operator--() noexcept;
    };

    inline module_lock& get_module_lock() noexcept
    {
        static module_lock instance;
        return instance;
    }
}

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.System.h>
