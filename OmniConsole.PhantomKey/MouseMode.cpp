#include "MouseMode.h"

// ============================================================================
// 手把滑鼠模式（Gamepad Mouse Mode）
// ============================================================================
//
// 注意：本模組直接呼叫 SendInput()，不重用 InputSender::SendKeyCombo
// （SendKeyCombo 在按下與放開間有 Sleep(50)，不適合 Mouse Mode 高頻使用）。
// ============================================================================

namespace {

    // ── 常數 ──────────────────────────────────────────────────────────────────

    constexpr float kDeadzone = 0.08f;

    // 游標速度（@ 100%）：線性，推多少動多少
    constexpr float kMaxSpeed = 18.0f;

    // 滾輪累積：每 tick 推進 kWheelStep，達 kWheelTriggerDelta 觸發一次系統事件
    // 注意：WHEEL_DELTA 是 Windows 系統巨集（120），故此處改名避免衝突
    constexpr float kWheelStep         = 8.0f;
    constexpr float kWheelTriggerDelta = 120.0f;

    // D-pad 長按連發
    constexpr ULONGLONG kLongPressInitialMs = 400;
    constexpr ULONGLONG kLongPressRepeatMs  = 50;

    // Trigger 邊緣觸發臨界值
    constexpr BYTE kTriggerThreshold = 128;

    // 搖桿最大值（XInput SHORT 範圍）
    constexpr float kStickMax = 32767.0f;

    // ── 內部狀態 ────────────────────────────────────────────────────────────────

    float cursorAccumX = 0.0f;  // sub-pixel 游標累積
    float cursorAccumY = 0.0f;
    float wheelAccumX = 0.0f;
    float wheelAccumY = 0.0f;
    WORD  prevButtons = 0;
    BYTE  prevLT = 0, prevRT = 0;

    struct RepeatState {
        bool active;
        ULONGLONG firstFireMs;
        ULONGLONG lastFireMs;
    };

    // 4 個方向各自 state：up/down/left/right
    RepeatState dpadRepeat[4] = {};

    // ── 工具函式 ────────────────────────────────────────────────────────────────

    // 將 XInput SHORT (-32768~32767) 正規化為 -1.0~1.0，套用圓形死區
    void NormalizeStick(SHORT rawX, SHORT rawY, float& nx, float& ny, float& mag) {
        float fx = (float)rawX / kStickMax;
        float fy = (float)rawY / kStickMax;
        float m = sqrtf(fx * fx + fy * fy);
        if (m < kDeadzone) {
            nx = ny = 0.0f;
            mag = 0.0f;
            return;
        }
        // 重新縮放：死區外從 0 開始重新對應到 1
        float scaled = (m - kDeadzone) / (1.0f - kDeadzone);
        if (scaled > 1.0f) scaled = 1.0f;
        nx = fx / m;   // 純方向（單位向量）
        ny = fy / m;
        mag = scaled;  // 推幅強度（0~1），由 HandleCursor 的 speed 曲線使用
    }

    void SendMouseMove(int dx, int dy) {
        if (dx == 0 && dy == 0) return;
        INPUT in = {};
        in.type = INPUT_MOUSE;
        in.mi.dx = dx;
        in.mi.dy = dy;
        in.mi.dwFlags = MOUSEEVENTF_MOVE;
        SendInput(1, &in, sizeof(INPUT));
    }

    void SendMouseWheel(int delta, bool horizontal) {
        INPUT in = {};
        in.type = INPUT_MOUSE;
        in.mi.mouseData = (DWORD)delta;
        in.mi.dwFlags = horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL;
        SendInput(1, &in, sizeof(INPUT));
    }

    void SendMouseButton(DWORD downFlag, DWORD upFlag, bool down) {
        INPUT in = {};
        in.type = INPUT_MOUSE;
        in.mi.dwFlags = down ? downFlag : upFlag;
        SendInput(1, &in, sizeof(INPUT));
    }

    void SendVKTap(WORD vk) {
        INPUT in[2] = {};
        in[0].type = INPUT_KEYBOARD;
        in[0].ki.wVk = vk;
        in[1].type = INPUT_KEYBOARD;
        in[1].ki.wVk = vk;
        in[1].ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput(2, in, sizeof(INPUT));
    }

    // 送出組合鍵 modifier+vk（modifier 可為 VK_CONTROL / VK_SHIFT / VK_MENU 之一）
    void SendVKCombo(WORD modifier, WORD vk) {
        INPUT in[4] = {};
        in[0].type = INPUT_KEYBOARD;  in[0].ki.wVk = modifier;
        in[1].type = INPUT_KEYBOARD;  in[1].ki.wVk = vk;
        in[2].type = INPUT_KEYBOARD;  in[2].ki.wVk = vk;        in[2].ki.dwFlags = KEYEVENTF_KEYUP;
        in[3].type = INPUT_KEYBOARD;  in[3].ki.wVk = modifier;  in[3].ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput(4, in, sizeof(INPUT));
    }

    // 送出三鍵組合 mod1+mod2+vk（例如 Ctrl+Shift+Tab）
    void SendVKCombo3(WORD mod1, WORD mod2, WORD vk) {
        INPUT in[6] = {};
        in[0].type = INPUT_KEYBOARD;  in[0].ki.wVk = mod1;
        in[1].type = INPUT_KEYBOARD;  in[1].ki.wVk = mod2;
        in[2].type = INPUT_KEYBOARD;  in[2].ki.wVk = vk;
        in[3].type = INPUT_KEYBOARD;  in[3].ki.wVk = vk;    in[3].ki.dwFlags = KEYEVENTF_KEYUP;
        in[4].type = INPUT_KEYBOARD;  in[4].ki.wVk = mod2;  in[4].ki.dwFlags = KEYEVENTF_KEYUP;
        in[5].type = INPUT_KEYBOARD;  in[5].ki.wVk = mod1;  in[5].ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput(6, in, sizeof(INPUT));
    }

    // ── 子處理函式 ───────────────────────────────────────────────────────────────

    void HandleCursor(SHORT rawX, SHORT rawY, int speedPercent) {
        float nx, ny, mag;
        NormalizeStick(rawX, rawY, nx, ny, mag);
        if (mag <= 0.0f) {
            cursorAccumX = cursorAccumY = 0.0f;
            return;
        }

        float speed = mag * kMaxSpeed * speedPercent / 100.0f;

        // sub-pixel 累積：保留小數，只在整數像素變化時送出
        cursorAccumX += nx * speed;
        cursorAccumY += -ny * speed;  // Y 軸反轉
        int dx = (int)cursorAccumX;
        int dy = (int)cursorAccumY;
        cursorAccumX -= dx;
        cursorAccumY -= dy;
        SendMouseMove(dx, dy);
    }

    void HandleScroll(SHORT rawX, SHORT rawY) {
        float nx, ny, mag;
        NormalizeStick(rawX, rawY, nx, ny, mag);
        if (mag <= 0.0f) return;

        wheelAccumX += nx * mag * kWheelStep;
        wheelAccumY += ny * mag * kWheelStep;

        while (wheelAccumY >= kWheelTriggerDelta)  { SendMouseWheel((int)kWheelTriggerDelta, false); wheelAccumY -= kWheelTriggerDelta; }
        while (wheelAccumY <= -kWheelTriggerDelta) { SendMouseWheel(-(int)kWheelTriggerDelta, false); wheelAccumY += kWheelTriggerDelta; }
        while (wheelAccumX >= kWheelTriggerDelta)  { SendMouseWheel((int)kWheelTriggerDelta, true);  wheelAccumX -= kWheelTriggerDelta; }
        while (wheelAccumX <= -kWheelTriggerDelta) { SendMouseWheel(-(int)kWheelTriggerDelta, true); wheelAccumX += kWheelTriggerDelta; }
    }

    void HandleButtons(WORD buttons, bool classic) {
        WORD changed = buttons ^ prevButtons;

        // A
        if (changed & XINPUT_GAMEPAD_A) {
            bool down = (buttons & XINPUT_GAMEPAD_A) != 0;
            if (classic) { if (down) SendVKTap(VK_RETURN); }
            else         { SendMouseButton(MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP, down); }
        }
        // B
        if (changed & XINPUT_GAMEPAD_B) {
            bool down = (buttons & XINPUT_GAMEPAD_B) != 0;
            if (classic) { if (down) SendVKTap(VK_ESCAPE); }
            else         { SendMouseButton(MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP, down); }
        }
        // X → PageDown
        if ((changed & XINPUT_GAMEPAD_X) && (buttons & XINPUT_GAMEPAD_X))
            SendVKTap(VK_NEXT);
        // Y → PageUp
        if ((changed & XINPUT_GAMEPAD_Y) && (buttons & XINPUT_GAMEPAD_Y))
            SendVKTap(VK_PRIOR);

        // LB
        if ((changed & XINPUT_GAMEPAD_LEFT_SHOULDER) && (buttons & XINPUT_GAMEPAD_LEFT_SHOULDER)) {
            if (classic) SendVKTap(VK_TAB);                                 // Tab
            else         SendVKCombo3(VK_CONTROL, VK_SHIFT, VK_TAB);        // Ctrl+Shift+Tab
        }
        // RB
        if (changed & XINPUT_GAMEPAD_RIGHT_SHOULDER) {
            bool down = (buttons & XINPUT_GAMEPAD_RIGHT_SHOULDER) != 0;
            if (classic) { SendMouseButton(MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP, down); }
            else         { if (down) SendVKCombo(VK_CONTROL, VK_TAB); }     // Ctrl+Tab
        }

        // LS 按下（OmniNav 專用：Shift+Tab）
        if (!classic && (changed & XINPUT_GAMEPAD_LEFT_THUMB) && (buttons & XINPUT_GAMEPAD_LEFT_THUMB))
            SendVKCombo(VK_SHIFT, VK_TAB);
        // RS 按下（OmniNav 專用：Tab）
        if (!classic && (changed & XINPUT_GAMEPAD_RIGHT_THUMB) && (buttons & XINPUT_GAMEPAD_RIGHT_THUMB))
            SendVKTap(VK_TAB);
    }

    void HandleTriggers(BYTE lt, BYTE rt, bool classic) {
        bool ltCross   = (lt >= kTriggerThreshold) && (prevLT < kTriggerThreshold);
        bool ltRelease = (lt < kTriggerThreshold)  && (prevLT >= kTriggerThreshold);
        bool rtCross   = (rt >= kTriggerThreshold) && (prevRT < kTriggerThreshold);
        bool rtRelease = (rt < kTriggerThreshold)  && (prevRT >= kTriggerThreshold);

        // LT
        if (classic) {
            if (ltCross) SendVKCombo(VK_SHIFT, VK_TAB);                     // Shift+Tab
        } else {
            if (ltCross) SendVKTap(VK_ESCAPE);
        }

        // RT
        if (classic) {
            // Classic：RT = 滑鼠右鍵（按住式）
            if (rtCross)   SendMouseButton(MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP, true);
            if (rtRelease) SendMouseButton(MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP, false);
        } else {
            if (rtCross) SendVKTap(VK_RETURN);
        }

        (void)ltRelease;  // 目前 LT 邊緣只在按下時觸發
    }

    // D-pad 連發處理：4 個方向獨立 RepeatState
    void HandleDpad(WORD buttons) {
        static const WORD masks[4] = {
            XINPUT_GAMEPAD_DPAD_UP,
            XINPUT_GAMEPAD_DPAD_DOWN,
            XINPUT_GAMEPAD_DPAD_LEFT,
            XINPUT_GAMEPAD_DPAD_RIGHT
        };
        static const WORD vks[4] = { VK_UP, VK_DOWN, VK_LEFT, VK_RIGHT };

        ULONGLONG now = GetTickCount64();
        for (int i = 0; i < 4; i++) {
            bool pressed = (buttons & masks[i]) != 0;
            RepeatState& s = dpadRepeat[i];
            if (pressed) {
                if (!s.active) {
                    // 首次按下：立即送一次
                    SendVKTap(vks[i]);
                    s.active = true;
                    s.firstFireMs = now;
                    s.lastFireMs = now;
                } else if (now - s.firstFireMs >= kLongPressInitialMs &&
                        now - s.lastFireMs  >= kLongPressRepeatMs) {
                    SendVKTap(vks[i]);
                    s.lastFireMs = now;
                }
            } else if (s.active) {
                s = {};
            }
        }
    }

    }  // anonymous namespace

    // ============================================================================
    // 公開介面
    // ============================================================================

    namespace MouseMode {

    void Tick(const XINPUT_GAMEPAD& pad, const AppConfig& cfg, bool skipDpad) {
        bool classic = (_wcsicmp(cfg.mouseModeLayout.c_str(), L"Classic") == 0);

        // 搖桿分配
        SHORT cursorX, cursorY, scrollX, scrollY;
        if (classic) {
            cursorX = pad.sThumbRX; cursorY = pad.sThumbRY;
            scrollX = pad.sThumbLX; scrollY = pad.sThumbLY;
        } else {
            // OmniNav 預設：左=游標、右=滾輪
            cursorX = pad.sThumbLX; cursorY = pad.sThumbLY;
            scrollX = pad.sThumbRX; scrollY = pad.sThumbRY;
        }

        HandleCursor(cursorX, cursorY, cfg.cursorSpeedPercent);
        HandleScroll(scrollX, scrollY);
        if (skipDpad) {
            // 前景對 D-pad 已有原生反應（如 Windows 11 檔案總管），不送方向鍵避免雙跳；
            // 同時清掉連發狀態，避免下次跨前景時殘留。
            for (auto& s : dpadRepeat) s = {};
        } else {
            HandleDpad(pad.wButtons);
        }
        HandleButtons(pad.wButtons, classic);
        HandleTriggers(pad.bLeftTrigger, pad.bRightTrigger, classic);

        prevButtons = pad.wButtons;
        prevLT = pad.bLeftTrigger;
        prevRT = pad.bRightTrigger;
    }

    void Reset() {
        cursorAccumX = cursorAccumY = 0.0f;
        wheelAccumX = wheelAccumY = 0.0f;
        prevButtons = 0;
        prevLT = prevRT = 0;
        for (auto& s : dpadRepeat) s = {};
    }

}  // namespace MouseMode
