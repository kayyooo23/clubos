#include "clubos_native.h"
#include "kiosk_guard.hpp"
#include "kiosk_rules.hpp"

#include <gtest/gtest.h>

#include <windows.h>

using namespace clubos;

namespace {

constexpr std::int32_t kLock = CLUBOS_KIOSK_LOCK, kSession = CLUBOS_KIOSK_SESSION;

// Нажатия клавиш в тестах НЕ имитируются (SendInput ушёл бы в окна живого пользователя): проверяется правило и включение/выключение хука.
void pump_messages() {
    MSG msg;
    while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
}

}  // namespace

TEST(KioskRules, TheWindowsKeysAreBlockedInBothProfiles) {
    for (const auto profile : {KioskProfile::Lock, KioskProfile::Session}) {
        EXPECT_TRUE(should_block(kVkLWin, false, false, profile));
        EXPECT_TRUE(should_block(kVkRWin, false, false, profile));
        EXPECT_TRUE(should_block(kVkLWin, true, true, profile));
    }
}

TEST(KioskRules, TaskManagerAndStartMenuShortcutsAreBlockedInBothProfiles) {
    for (const auto profile : {KioskProfile::Lock, KioskProfile::Session}) {
        EXPECT_TRUE(should_block(kVkEscape, false, true, profile));  // Ctrl+Esc и Ctrl+Shift+Esc (Shift в правило не входит)
    }
}

TEST(KioskRules, AltTabAltEscAndAltF4AreBlockedOnlyOnTheLockScreen) {
    for (const auto vk : {kVkTab, kVkEscape, kVkF4}) {
        EXPECT_TRUE(should_block(vk, true, false, KioskProfile::Lock)) << vk;
        EXPECT_FALSE(should_block(vk, true, false, KioskProfile::Session)) << vk;  // игре они нужны: иначе её не закрыть
    }
}

TEST(KioskRules, OrdinaryKeysAreNeverBlocked) {
    for (const auto profile : {KioskProfile::Lock, KioskProfile::Session}) {
        EXPECT_FALSE(should_block(kVkTab, false, false, profile));
        EXPECT_FALSE(should_block(kVkEscape, false, false, profile));
        EXPECT_FALSE(should_block(kVkF4, false, false, profile));
        EXPECT_FALSE(should_block(kVkF12, true, true, profile));
        EXPECT_FALSE(should_block('A', true, true, profile));  // Ctrl+Alt+A и подобное не трогаем
    }
}

TEST(KioskRules, ExhaustiveOverEveryKeyAndModifier) {
    for (std::int32_t vk = 0; vk < 256; ++vk) {
        for (const bool alt : {false, true}) {
            for (const bool ctrl : {false, true}) {
                for (const auto profile : {KioskProfile::Lock, KioskProfile::Session}) {
                    const bool win = vk == 0x5B || vk == 0x5C;
                    const bool alt_exit = alt && (vk == 0x09 || vk == 0x1B || vk == 0x73);
                    const bool ctrl_esc = ctrl && vk == 0x1B;
                    const bool expected = win || (profile == KioskProfile::Lock && alt_exit) || ctrl_esc;
                    ASSERT_EQ(should_block(vk, alt, ctrl, profile), expected) << "vk=" << vk << " alt=" << alt << " ctrl=" << ctrl;
                }
            }
        }
    }
}

TEST(KioskRules, TheExportedRuleIsTheSameAsTheInternalOne) {
    for (std::int32_t vk = 0; vk < 256; ++vk)
        for (const int alt : {0, 1})
            for (const int ctrl : {0, 1})
                for (const int shift : {0, 1})
                    for (const auto profile : {kLock, kSession})
                        ASSERT_EQ(clubos_kiosk_should_block(vk, alt, ctrl, shift, profile) != 0,
                                  should_block(vk, alt != 0, ctrl != 0, static_cast<KioskProfile>(profile)));
}

TEST(ExitPresses, ThreeWithinThreeSecondsTrigger) {
    std::deque<std::uint64_t> presses;
    EXPECT_FALSE(register_exit_press(presses, 1000));
    EXPECT_FALSE(register_exit_press(presses, 1500));
    EXPECT_TRUE(register_exit_press(presses, 2000));
}

TEST(ExitPresses, SlowPressesDoNot) {
    std::deque<std::uint64_t> presses;
    EXPECT_FALSE(register_exit_press(presses, 0));
    EXPECT_FALSE(register_exit_press(presses, 2000));
    EXPECT_FALSE(register_exit_press(presses, 4000));   // первое уже старше 3 секунд
    EXPECT_FALSE(register_exit_press(presses, 6500));
    EXPECT_FALSE(register_exit_press(presses, 9000));   // между нажатиями всегда больше полутора секунд: трёх подряд за 3 секунды нет
}

TEST(ExitPresses, TheBoundaryIsInclusiveAtThreeSeconds) {
    std::deque<std::uint64_t> presses;
    register_exit_press(presses, 0);
    register_exit_press(presses, 1000);
    EXPECT_TRUE(register_exit_press(presses, 3000));     // ровно 3000 мс от первого: ещё считается
    EXPECT_TRUE(register_exit_press(presses, 3001));     // первое ушло, но осталось три (1000, 3000, 3001)
}

TEST(KioskHook, EnableAndDisableAreIdempotentAndReportTheState) {
    kiosk_disable();                       // выключено и так: ничего не случается
    EXPECT_FALSE(kiosk_active());
    const auto first = kiosk_enable(KioskProfile::Lock, false, nullptr);
    ASSERT_TRUE(first.ok) << "код Windows " << first.os_error;
    pump_messages();
    EXPECT_TRUE(kiosk_active());
    EXPECT_TRUE(kiosk_enable(KioskProfile::Session, false, nullptr).ok);  // второй раз: хук тот же, меняются только настройки
    EXPECT_TRUE(kiosk_active());
    kiosk_set_profile(KioskProfile::Lock);
    kiosk_disable();
    kiosk_disable();
    EXPECT_FALSE(kiosk_active());
}

TEST(KioskHook, TheExportedFunctionsFollowTheSameLifecycle) {
    uint32_t os_error = 99;
    ASSERT_EQ(clubos_kiosk_enable(kSession, 0, nullptr, &os_error), CLUBOS_OK);
    EXPECT_EQ(os_error, 0u);
    pump_messages();
    EXPECT_EQ(clubos_kiosk_active(), 1);
    clubos_kiosk_set_profile(kLock);
    clubos_kiosk_set_profile(77);          // неизвестный набор игнорируется
    clubos_kiosk_disable();
    EXPECT_EQ(clubos_kiosk_active(), 0);
    EXPECT_EQ(clubos_kiosk_enable(5, 0, nullptr, nullptr), CLUBOS_ERR_ARGS);
    EXPECT_EQ(clubos_kiosk_active(), 0);
}
