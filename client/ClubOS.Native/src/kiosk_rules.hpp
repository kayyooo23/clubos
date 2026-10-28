// Правила перехвата клавиш киоска: чистые функции без обращений к системе, поэтому проверяются перебором всех сочетаний.
#pragma once

#include <cstdint>
#include <deque>

namespace clubos {

enum class KioskProfile : std::int32_t {
    Lock = 0,     // экран блокировки: закрыты все пути с экрана
    Session = 1,  // идёт сессия гостя: закрыты только пути на рабочий стол Windows, игре нужны остальные клавиши
};

// Коды клавиш Windows (VK_*).
inline constexpr std::int32_t kVkTab = 0x09, kVkEscape = 0x1B, kVkF4 = 0x73, kVkF12 = 0x7B, kVkLWin = 0x5B, kVkRWin = 0x5C;

// Нужно ли проглотить нажатие. Win закрыта всегда; Ctrl+Esc и Ctrl+Shift+Esc (диспетчер задач) тоже; Alt+Tab, Alt+Esc и Alt+F4 только на экране блокировки:
// в игре они нужны, иначе игру не закрыть и из неё не вернуться в меню.
constexpr bool should_block(std::int32_t vk, bool alt, bool ctrl, KioskProfile profile) noexcept {
    return vk == kVkLWin || vk == kVkRWin  //
           || (profile == KioskProfile::Lock && alt && (vk == kVkTab || vk == kVkEscape || vk == kVkF4))  //
           || (ctrl && vk == kVkEscape);
}

// Аварийный выход: Ctrl+Shift+F12 три раза за три секунды. Запоминает нажатие и говорит, набралось ли три за последние 3000 мс.
inline bool register_exit_press(std::deque<std::uint64_t>& presses, std::uint64_t now_ms) {
    presses.push_back(now_ms);
    while (!presses.empty() && now_ms - presses.front() > 3000) presses.pop_front();
    return presses.size() >= 3;
}

}  // namespace clubos
