// Перехват клавиш киоска: низкоуровневый хук клавиатуры (WH_KEYBOARD_LL). Один хук на процесс.
// Все функции вызываются из одного потока с циклом сообщений (у WPF это главный поток): Windows вызывает хук именно в нём.
#pragma once

#include <cstdint>

#include "kiosk_rules.hpp"

namespace clubos {

using ExitCallback = void(__cdecl*)();

struct KioskResult {
    bool ok = true;
    std::uint32_t os_error = 0;  // GetLastError, если хук не встал
};

// Включает перехват. Уже включён: меняет набор клавиш и настройки аварийного выхода, хук не ставится второй раз.
KioskResult kiosk_enable(KioskProfile profile, bool allow_exit, ExitCallback on_exit) noexcept;
void kiosk_set_profile(KioskProfile profile) noexcept;
void kiosk_disable() noexcept;
bool kiosk_active() noexcept;

}  // namespace clubos
