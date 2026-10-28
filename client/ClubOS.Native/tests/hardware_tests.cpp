#include "clubos_native.h"
#include "hardware.hpp"

#include <gtest/gtest.h>

#include <cwctype>

using namespace clubos;

TEST(ToGb, RoundsToTheNearestGigabyteAndClamps) {
    constexpr std::uint64_t kGb = 1073741824ull;
    EXPECT_EQ(to_gb(0), 0);
    EXPECT_EQ(to_gb(kGb), 1);
    EXPECT_EQ(to_gb(kGb / 2 - 1), 0);
    EXPECT_EQ(to_gb(kGb + kGb / 2), 2);        // 1,5 ГБ округляется вверх, как в отчёте сервера
    EXPECT_EQ(to_gb(16 * kGb - kGb / 10), 16);  // «почти 16» это 16
    EXPECT_EQ(to_gb(~0ull), 1'000'000);         // больше миллиона не бывает
}

TEST(Mac, FormatsSixBytesWithDashesInUpperCase) {
    const std::uint8_t mac[] = {0x00, 0x1a, 0x2B, 0xff, 0x10, 0x0c};
    EXPECT_EQ(format_mac(mac, 6), L"00-1A-2B-FF-10-0C");
    EXPECT_TRUE(format_mac(mac, 5).empty());
    EXPECT_TRUE(format_mac(mac, 8).empty());
    EXPECT_TRUE(format_mac(nullptr, 6).empty());
}

TEST(ChooseGpu, PrefersARealDiscreteCardOverTheIntegratedOne) {
    EXPECT_EQ(choose_gpu({L"Intel(R) UHD Graphics 770", L"NVIDIA GeForce RTX 4070 SUPER"}), L"NVIDIA GeForce RTX 4070 SUPER");
    EXPECT_EQ(choose_gpu({L"Intel(R) UHD Graphics", L"AMD Radeon RX 7800 XT"}), L"AMD Radeon RX 7800 XT");
    EXPECT_EQ(choose_gpu({L"nvidia geforce gtx 1650"}), L"nvidia geforce gtx 1650");   // без учёта регистра
}

TEST(ChooseGpu, IgnoresTheBasicMicrosoftDriverAndEmptyNames) {
    EXPECT_EQ(choose_gpu({L"Microsoft Basic Display Adapter", L"Intel(R) UHD Graphics 770"}), L"Intel(R) UHD Graphics 770");
    EXPECT_EQ(choose_gpu({L"", L"   ", L"Intel(R) Iris(R) Xe Graphics"}), L"Intel(R) Iris(R) Xe Graphics");
    EXPECT_TRUE(choose_gpu({L"Microsoft Basic Render Driver"}).empty());
    EXPECT_TRUE(choose_gpu({}).empty());
}

TEST(CpuName, IsReadableAndHasNoDoubleSpaces) {
    const auto name = cpu_name();
    if (name.empty()) GTEST_SKIP() << "процессор не сообщает название";
    EXPECT_EQ(name.find(L"  "), std::wstring::npos);
    EXPECT_NE(name.front(), L' ');
    EXPECT_NE(name.back(), L' ');
    EXPECT_GE(name.size(), 4u);
}

TEST(Memory, InstalledMemoryIsPositiveAndPlausible) {
    const auto gb = installed_ram_gb();
    EXPECT_GE(gb, 1);
    EXPECT_LE(gb, 16384);
}

TEST(Disks, FixedDisksHaveALetterAndFreeSpaceNotAboveTheTotal) {
    const auto disks = fixed_disks(16);
    ASSERT_FALSE(disks.empty()) << "на ПК нет ни одного фиксированного диска";
    for (const auto& d : disks) {
        ASSERT_EQ(d.name.size(), 2u);
        EXPECT_EQ(d.name[1], L':');
        EXPECT_TRUE(std::iswupper(d.name[0]));
        EXPECT_GT(d.total_gb, 0);
        EXPECT_LE(d.free_gb, d.total_gb);
    }
    EXPECT_LE(fixed_disks(1).size(), 1u);   // ограничение числа дисков соблюдается
}

TEST(Network, AFoundAdapterHasAnIpv4AddressAndASixByteMac) {
    const auto net = network_info();
    if (!net) GTEST_SKIP() << "на ПК нет рабочего сетевого адаптера";
    int dots = 0;
    for (const wchar_t ch : net->ip) dots += ch == L'.';
    EXPECT_EQ(dots, 3);
    EXPECT_NE(net->ip.rfind(L"169.254", 0), 0u);          // адреса без сети (APIPA) не берутся
    if (!net->mac.empty()) EXPECT_EQ(net->mac.size(), 17u);
}

TEST(Hardware, TheCombinedReportKeepsEachPartIndependent) {
    const auto info = collect_hardware(CLUBOS_MAX_DISKS);
    EXPECT_GE(info.ram_gb, 1);
    EXPECT_FALSE(info.disks.empty());
}

TEST(HardwareExports, FillTheStructureForTheCaller) {
    clubos_hardware hw{};
    ASSERT_EQ(clubos_collect_hardware(&hw), CLUBOS_OK);
    EXPECT_GE(hw.ram_gb, 1);
    EXPECT_GE(hw.disk_count, 1);
    EXPECT_LE(hw.disk_count, CLUBOS_MAX_DISKS);
    EXPECT_EQ(hw.disks[0].name[1], L':');
    EXPECT_EQ(clubos_collect_hardware(nullptr), CLUBOS_ERR_ARGS);

    wchar_t ip[48] = {}, mac[24] = {};
    const int found = clubos_network(ip, mac);
    if (found) {
        EXPECT_GT(wcslen(ip), 6u);
        EXPECT_STREQ(ip, hw.ip);
    } else {
        EXPECT_EQ(wcslen(ip), 0u);
    }
    EXPECT_EQ(clubos_network(nullptr, mac), 0);
}
