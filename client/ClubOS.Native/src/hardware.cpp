#include "hardware.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cwctype>

#include <winsock2.h>
#include <ws2tcpip.h>
#include <iphlpapi.h>
#include <windows.h>

#if defined(_M_X64) || defined(_M_IX86)
#include <intrin.h>
#endif

namespace clubos {
namespace {

constexpr wchar_t kGpuClassKey[] = L"SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e968-e325-11ce-bfc1-08002be10318}";
constexpr std::uint64_t kBytesInGb = 1073741824ull;
constexpr std::int32_t kMaxGb = 1'000'000;

std::wstring lower(std::wstring text) {
    for (auto& ch : text) ch = static_cast<wchar_t>(std::towlower(ch));
    return text;
}

bool contains(const std::wstring& text, const wchar_t* part) { return lower(text).find(lower(part)) != std::wstring::npos; }

// Убирает пробелы по краям и схлопывает подряд идущие (у процессоров в названии бывают двойные).
std::wstring collapse_spaces(const std::wstring& text) {
    std::wstring result;
    bool space = false;
    for (const wchar_t ch : text) {
        if (ch == L' ' || ch == L'\t' || ch == L'\0') {
            space = !result.empty();
            continue;
        }
        if (space) result += L' ';
        space = false;
        result += ch;
    }
    return result;
}

class RegistryKey {
public:
    RegistryKey(HKEY parent, const std::wstring& path) noexcept {
        if (RegOpenKeyExW(parent, path.c_str(), 0, KEY_READ, &key_) != ERROR_SUCCESS) key_ = nullptr;
    }
    RegistryKey(const RegistryKey&) = delete;
    RegistryKey& operator=(const RegistryKey&) = delete;
    ~RegistryKey() {
        if (key_ != nullptr) RegCloseKey(key_);
    }
    HKEY get() const noexcept { return key_; }
    explicit operator bool() const noexcept { return key_ != nullptr; }

private:
    HKEY key_ = nullptr;
};

std::wstring read_string(HKEY key, const wchar_t* name) {
    DWORD bytes = 0;
    if (RegGetValueW(key, nullptr, name, RRF_RT_REG_SZ, nullptr, nullptr, &bytes) != ERROR_SUCCESS || bytes < sizeof(wchar_t)) return {};
    std::wstring value(bytes / sizeof(wchar_t), L'\0');
    if (RegGetValueW(key, nullptr, name, RRF_RT_REG_SZ, nullptr, value.data(), &bytes) != ERROR_SUCCESS) return {};
    value.resize(wcsnlen(value.c_str(), value.size()));
    return value;
}

bool all_digits(const std::wstring& text) {
    return !text.empty() && std::all_of(text.begin(), text.end(), [](wchar_t ch) { return ch >= L'0' && ch <= L'9'; });
}

}  // namespace

std::int32_t to_gb(std::uint64_t bytes) noexcept {
    const auto gb = static_cast<std::int64_t>(std::llround(static_cast<double>(bytes) / static_cast<double>(kBytesInGb)));
    return static_cast<std::int32_t>(std::clamp<std::int64_t>(gb, 0, kMaxGb));
}

std::wstring cpu_name() {
#if defined(_M_X64) || defined(_M_IX86)
    int info[4] = {};
    __cpuid(info, static_cast<int>(0x80000000));
    if (static_cast<unsigned>(info[0]) < 0x80000004u) return {};  // процессор не сообщает название
    std::array<char, 49> brand{};
    for (unsigned i = 0; i < 3; ++i) {
        __cpuid(info, static_cast<int>(0x80000002u + i));
        std::copy_n(reinterpret_cast<const char*>(info), 16, brand.data() + i * 16);
    }
    std::wstring name;
    for (const char ch : brand) {
        if (ch == '\0') break;
        name += static_cast<wchar_t>(static_cast<unsigned char>(ch));
    }
    return collapse_spaces(name);
#else
    return {};
#endif
}

std::int32_t installed_ram_gb() noexcept {
    ULONGLONG kilobytes = 0;
    if (GetPhysicallyInstalledSystemMemory(&kilobytes) && kilobytes > 0) return to_gb(kilobytes * 1024ull);
    MEMORYSTATUSEX status{};
    status.dwLength = sizeof status;
    return GlobalMemoryStatusEx(&status) ? to_gb(status.ullTotalPhys) : 0;
}

std::wstring choose_gpu(const std::vector<std::wstring>& driver_names) {
    std::vector<std::wstring> names;
    for (const auto& name : driver_names)
        if (!collapse_spaces(name).empty() && !contains(name, L"Basic")) names.push_back(name);
    for (const auto& name : names)
        if (contains(name, L"NVIDIA") || contains(name, L"Radeon") || contains(name, L"GeForce")) return name;
    return names.empty() ? std::wstring() : names.front();
}

std::wstring gpu_name() {
    const RegistryKey cls(HKEY_LOCAL_MACHINE, kGpuClassKey);
    if (!cls) return {};
    std::vector<std::wstring> names;
    for (DWORD index = 0;; ++index) {
        wchar_t sub[256] = {};
        DWORD length = static_cast<DWORD>(std::size(sub));
        if (RegEnumKeyExW(cls.get(), index, sub, &length, nullptr, nullptr, nullptr, nullptr) != ERROR_SUCCESS) break;
        const std::wstring sub_name(sub, length);
        if (!all_digits(sub_name)) continue;
        const RegistryKey adapter(cls.get(), sub_name);
        if (adapter) names.push_back(read_string(adapter.get(), L"DriverDesc"));
    }
    return choose_gpu(names);
}

std::vector<DiskInfo> fixed_disks(std::size_t max) {
    std::vector<DiskInfo> disks;
    DWORD mask = GetLogicalDrives();
    for (wchar_t letter = L'A'; letter <= L'Z' && disks.size() < max; ++letter, mask >>= 1) {
        if ((mask & 1) == 0) continue;
        const std::wstring root = std::wstring(1, letter) + L":\\";
        if (GetDriveTypeW(root.c_str()) != DRIVE_FIXED) continue;
        ULARGE_INTEGER available{}, total{}, free_total{};
        if (!GetDiskFreeSpaceExW(root.c_str(), &available, &total, &free_total)) continue;  // диск не готов или отвалился: пропускаем
        disks.push_back({std::wstring(1, letter) + L":", to_gb(total.QuadPart), to_gb(available.QuadPart)});
    }
    return disks;
}

std::wstring format_mac(const std::uint8_t* bytes, std::size_t size) {
    if (bytes == nullptr || size != 6) return {};
    static constexpr wchar_t kDigits[] = L"0123456789ABCDEF";
    std::wstring mac;
    for (std::size_t i = 0; i < size; ++i) {
        if (i > 0) mac += L'-';
        mac += kDigits[bytes[i] >> 4];
        mac += kDigits[bytes[i] & 15];
    }
    return mac;
}

std::optional<NetworkInfo> network_info() {
    ULONG size = 16 * 1024;
    std::vector<std::uint8_t> buffer;
    ULONG result = ERROR_BUFFER_OVERFLOW;
    constexpr ULONG flags = GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER;
    for (int attempt = 0; attempt < 3 && result == ERROR_BUFFER_OVERFLOW; ++attempt) {
        buffer.assign(size, 0);
        result = GetAdaptersAddresses(AF_INET, flags, nullptr, reinterpret_cast<PIP_ADAPTER_ADDRESSES>(buffer.data()), &size);
    }
    if (result != NO_ERROR) return std::nullopt;

    std::optional<NetworkInfo> wired, wireless;
    for (auto* adapter = reinterpret_cast<PIP_ADAPTER_ADDRESSES>(buffer.data()); adapter != nullptr; adapter = adapter->Next) {
        const bool is_wired = adapter->IfType == IF_TYPE_ETHERNET_CSMACD;
        if (adapter->OperStatus != IfOperStatusUp || !(is_wired || adapter->IfType == IF_TYPE_IEEE80211)) continue;
        const std::wstring description = adapter->Description != nullptr ? adapter->Description : L"";
        if (contains(description, L"Virtual") || contains(description, L"VPN") || contains(description, L"TAP")) continue;
        for (auto* address = adapter->FirstUnicastAddress; address != nullptr; address = address->Next) {
            if (address->Address.lpSockaddr == nullptr || address->Address.lpSockaddr->sa_family != AF_INET) continue;
            const auto* v4 = reinterpret_cast<const sockaddr_in*>(address->Address.lpSockaddr);
            const auto* octets = reinterpret_cast<const std::uint8_t*>(&v4->sin_addr);
            if (octets[0] == 169 && octets[1] == 254) continue;
            wchar_t text[INET_ADDRSTRLEN] = {};
            if (InetNtopW(AF_INET, &v4->sin_addr, text, std::size(text)) == nullptr) continue;
            NetworkInfo found{text, format_mac(adapter->PhysicalAddress, adapter->PhysicalAddressLength)};
            auto& slot = is_wired ? wired : wireless;
            if (!slot) slot = std::move(found);
            break;
        }
        if (wired) break;  // проводной в приоритете: дальше смотреть незачем
    }
    return wired ? wired : wireless;
}

HardwareInfo collect_hardware(std::size_t max_disks) {
    HardwareInfo info;
    // каждая часть защищена отдельно: сбой в одной не отнимает остальные
    try { info.cpu = cpu_name(); } catch (...) {}
    try { info.ram_gb = installed_ram_gb(); } catch (...) {}
    try { info.gpu = gpu_name(); } catch (...) {}
    try { info.disks = fixed_disks(max_disks); } catch (...) {}
    try { info.network = network_info(); } catch (...) {}
    return info;
}

}  // namespace clubos
