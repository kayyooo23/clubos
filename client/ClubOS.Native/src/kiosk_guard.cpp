#include "kiosk_guard.hpp"

#include <atomic>
#include <deque>

#include <windows.h>

namespace clubos {
namespace {

constexpr DWORD kAltDownFlag = LLKHF_ALTDOWN;

std::atomic<std::int32_t> g_profile{static_cast<std::int32_t>(KioskProfile::Lock)};
std::atomic<bool> g_allow_exit{false};
std::atomic<ExitCallback> g_on_exit{nullptr};
HHOOK g_hook = nullptr;                // ставится и снимается в одном потоке (см. kiosk_guard.hpp)
std::deque<std::uint64_t> g_presses;   // нажатия аварийного выхода: трогает только поток хука

bool key_down(int vk) noexcept { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

LRESULT CALLBACK keyboard_proc(int code, WPARAM wparam, LPARAM lparam) {
    if (code >= 0 && (wparam == WM_KEYDOWN || wparam == WM_SYSKEYDOWN)) {
        try {
            const auto* key = reinterpret_cast<const KBDLLHOOKSTRUCT*>(lparam);
            const auto vk = static_cast<std::int32_t>(key->vkCode);
            const bool alt = (key->flags & kAltDownFlag) != 0 || key_down(VK_MENU);
            const bool ctrl = key_down(VK_CONTROL);
            const bool shift = key_down(VK_SHIFT);

            if (g_allow_exit.load() && vk == kVkF12 && ctrl && shift && register_exit_press(g_presses, GetTickCount64())) {
                g_presses.clear();
                if (const ExitCallback callback = g_on_exit.load()) callback();
            }
            if (should_block(vk, alt, ctrl, static_cast<KioskProfile>(g_profile.load()))) return 1;  // проглатываем нажатие
        } catch (...) {
            // хук не должен ронять процесс: при сбое пропускаем нажатие дальше
        }
    }
    return CallNextHookEx(g_hook, code, wparam, lparam);
}

// Модуль, в котором лежит код хука (эта DLL или тестовая программа): Windows привязывает хук к нему.
HMODULE this_module() noexcept {
    HMODULE module = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       reinterpret_cast<LPCWSTR>(&keyboard_proc), &module);
    return module;
}

}  // namespace

KioskResult kiosk_enable(KioskProfile profile, bool allow_exit, ExitCallback on_exit) noexcept {
    g_profile.store(static_cast<std::int32_t>(profile));
    g_allow_exit.store(allow_exit);
    g_on_exit.store(on_exit);
    if (g_hook != nullptr) return {};
    g_hook = SetWindowsHookExW(WH_KEYBOARD_LL, keyboard_proc, this_module(), 0);
    if (g_hook == nullptr) return {false, GetLastError()};
    g_presses.clear();
    return {};
}

void kiosk_set_profile(KioskProfile profile) noexcept { g_profile.store(static_cast<std::int32_t>(profile)); }

void kiosk_disable() noexcept {
    if (g_hook == nullptr) return;
    UnhookWindowsHookEx(g_hook);
    g_hook = nullptr;
    g_presses.clear();
}

bool kiosk_active() noexcept { return g_hook != nullptr; }

}  // namespace clubos
