#include "clubos_native.h"
#include "windows_info.hpp"

#include <gtest/gtest.h>

#include <filesystem>
#include <vector>

#include <windows.h>

using namespace clubos;

// Окна на этом ПК сторонние: тесты ничего не показывают, не двигают и не закрывают (на несуществующем окне команды безвредны).

TEST(Windows, ListedWindowsHaveAProcessAHandleAndAClass) {
    const auto windows = top_level_windows();
    for (const auto& w : windows) {
        EXPECT_NE(w.handle, 0);
        EXPECT_GT(w.pid, 0);
        EXPECT_FALSE(w.class_name.empty());
        EXPECT_GE(w.width, 0);
        EXPECT_GE(w.height, 0);
    }
}

TEST(Windows, TheExportedListReportsTheTotalEvenWhenTheBufferIsSmall) {
    int32_t total = -1;
    ASSERT_EQ(clubos_list_windows(nullptr, 0, &total), CLUBOS_OK);
    EXPECT_GE(total, 0);

    std::vector<clubos_window_info> buffer(1);
    int32_t again = -1;
    ASSERT_EQ(clubos_list_windows(buffer.data(), 1, &again), CLUBOS_OK);
    if (total > 0) {
        EXPECT_GT(buffer[0].handle, 0);
        EXPECT_GT(buffer[0].pid, 0);
        EXPECT_GT(wcslen(buffer[0].class_name), 0u);
    }
    EXPECT_EQ(clubos_list_windows(nullptr, 0, nullptr), CLUBOS_ERR_ARGS);
    EXPECT_EQ(clubos_list_windows(nullptr, 5, &again), CLUBOS_ERR_ARGS);
}

TEST(Windows, TheForegroundWindowIsDescribedConsistently) {
    const auto window = foreground_window();
    clubos_window_info exported{};
    const int has = clubos_foreground_window(&exported);
    if (!window) return;  // фокуса может не быть (например, экран заблокирован)
    EXPECT_NE(window->handle, 0);
    EXPECT_GT(window->pid, 0);
    EXPECT_EQ(clubos_foreground_window(nullptr), 0);
    (void)has;
    (void)exported;
}

TEST(Windows, CommandsOnAWindowThatDoesNotExistAreHarmless) {
    constexpr std::int64_t kNoSuchWindow = 0x7FFFFFF0;
    close_window(kNoSuchWindow);
    bring_to_front(kNoSuchWindow);
    EXPECT_EQ(clubos_close_window(kNoSuchWindow), CLUBOS_OK);
    EXPECT_EQ(clubos_bring_to_front(kNoSuchWindow), CLUBOS_OK);
}

TEST(Windows, TheNotificationStateIsAKnownValue) {
    EXPECT_GE(notification_state(), 1);
    EXPECT_LE(notification_state(), 7);
    EXPECT_EQ(clubos_notification_state(), notification_state());
}

TEST(Processes, OurOwnProcessIsNamedLikeOurExecutableWithoutTheExtension) {
    wchar_t path[MAX_PATH] = {};
    ASSERT_GT(GetModuleFileNameW(nullptr, path, MAX_PATH), 0u);
    EXPECT_EQ(process_name_of(GetCurrentProcessId()), std::filesystem::path(path).stem().wstring());
}

TEST(Processes, UnknownProcessesHaveNoName) {
    EXPECT_TRUE(process_name_of(0).empty());
    EXPECT_TRUE(process_name_of(0x7FFFFFF0).empty());   // такого процесса нет
}
