// Опрос окон и процессов для оболочки гостя: какие окна открыты, какое в фокусе, вывести вперёд, закрыть.
#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace clubos {

struct WindowInfo {
    std::int64_t handle = 0;
    std::int32_t pid = 0;
    std::int32_t width = 0;
    std::int32_t height = 0;
    bool minimized = false;
    std::wstring process_name;  // имя exe без расширения; пусто, если процесс закрылся или нет доступа
    std::wstring class_name;
};

// Видимые окна верхнего уровня, самое верхнее первым: без вспомогательных (у них есть владелец), «инструментальных» и скрытых UWP-окон.
std::vector<WindowInfo> top_level_windows();
std::optional<WindowInfo> foreground_window();
void bring_to_front(std::int64_t handle) noexcept;
void close_window(std::int64_t handle) noexcept;
// SHQueryUserNotificationState: 2 и 3 полноэкранное приложение, 5 можно показывать уведомления. При сбое 5.
std::int32_t notification_state() noexcept;

// Имя exe процесса без пути и расширения (пусто, если не открылся).
std::wstring process_name_of(std::uint32_t pid);

}  // namespace clubos
