// Экспортируемые функции DLL (интерфейс на C, см. include/clubos_native.h). Вся работа в остальных файлах src/:
// сюда попадает только перекладывание данных в структуры для C# и перехват исключений, чтобы они не вышли за границу DLL.
#include "clubos_native.h"

#include <algorithm>
#include <span>
#include <string>

#include "block_hasher.hpp"
#include "hardware.hpp"
#include "kiosk_guard.hpp"
#include "windows_info.hpp"

namespace {

// Копирует строку в массив фиксированного размера, обрезая лишнее; в конце всегда нулевой знак.
template <std::size_t N>
void copy_text(wchar_t (&dst)[N], const std::wstring& src) {
    const auto length = std::min(src.size(), N - 1);
    std::copy_n(src.data(), length, dst);
    dst[length] = L'\0';
}

void fill(clubos_window_info& out, const clubos::WindowInfo& in) {
    out.handle = in.handle;
    out.pid = in.pid;
    out.width = in.width;
    out.height = in.height;
    out.minimized = in.minimized ? 1 : 0;
    copy_text(out.process_name, in.process_name);
    copy_text(out.class_name, in.class_name);
}

}  // namespace

extern "C" {

CLUBOS_API int32_t clubos_abi_version(void) { return CLUBOS_ABI_VERSION; }

// ---------- хеши блоков ----------
CLUBOS_API int32_t clubos_hash_blocks(const wchar_t* path, int64_t expected_length, int32_t block_size, uint8_t* out_digests,
                                      int64_t out_capacity, const volatile int32_t* cancel, uint32_t* os_error) {
    if (os_error != nullptr) *os_error = 0;
    if (path == nullptr || out_capacity < 0 || (out_digests == nullptr && out_capacity != 0)) return CLUBOS_ERR_ARGS;
    try {
        const auto result = clubos::hash_file_blocks(std::wstring(path), expected_length, block_size,
                                                     std::span<std::uint8_t>(out_digests, static_cast<std::size_t>(out_capacity)), cancel);
        if (os_error != nullptr) *os_error = result.os_error;
        return static_cast<int32_t>(result.status);
    } catch (...) {
        return CLUBOS_ERR_INTERNAL;
    }
}

// ---------- перехват клавиш ----------
CLUBOS_API int32_t clubos_kiosk_enable(int32_t profile, int32_t allow_exit, clubos_exit_callback on_exit, uint32_t* os_error) {
    if (os_error != nullptr) *os_error = 0;
    if (profile != CLUBOS_KIOSK_LOCK && profile != CLUBOS_KIOSK_SESSION) return CLUBOS_ERR_ARGS;
    const auto result = clubos::kiosk_enable(static_cast<clubos::KioskProfile>(profile), allow_exit != 0, on_exit);
    if (os_error != nullptr) *os_error = result.os_error;
    return result.ok ? CLUBOS_OK : CLUBOS_ERR_OPEN;
}

CLUBOS_API void clubos_kiosk_set_profile(int32_t profile) {
    if (profile == CLUBOS_KIOSK_LOCK || profile == CLUBOS_KIOSK_SESSION) clubos::kiosk_set_profile(static_cast<clubos::KioskProfile>(profile));
}

CLUBOS_API void clubos_kiosk_disable(void) { clubos::kiosk_disable(); }

CLUBOS_API int32_t clubos_kiosk_active(void) { return clubos::kiosk_active() ? 1 : 0; }

CLUBOS_API int32_t clubos_kiosk_should_block(int32_t vk, int32_t alt, int32_t ctrl, int32_t /*shift*/, int32_t profile) {
    return clubos::should_block(vk, alt != 0, ctrl != 0, profile == CLUBOS_KIOSK_SESSION ? clubos::KioskProfile::Session : clubos::KioskProfile::Lock) ? 1 : 0;
}

// ---------- окна и процессы ----------
CLUBOS_API int32_t clubos_list_windows(clubos_window_info* out, int32_t capacity, int32_t* count) {
    if (count == nullptr || capacity < 0 || (out == nullptr && capacity != 0)) return CLUBOS_ERR_ARGS;
    *count = 0;
    try {
        const auto windows = clubos::top_level_windows();
        *count = static_cast<int32_t>(windows.size());
        for (std::size_t i = 0; i < windows.size() && i < static_cast<std::size_t>(capacity); ++i) fill(out[i], windows[i]);
        return CLUBOS_OK;
    } catch (...) {
        return CLUBOS_ERR_INTERNAL;
    }
}

CLUBOS_API int32_t clubos_foreground_window(clubos_window_info* out) {
    if (out == nullptr) return 0;
    try {
        const auto window = clubos::foreground_window();
        if (!window) return 0;
        fill(*out, *window);
        return 1;
    } catch (...) {
        return 0;
    }
}

CLUBOS_API int32_t clubos_bring_to_front(int64_t handle) {
    clubos::bring_to_front(handle);
    return CLUBOS_OK;
}

CLUBOS_API int32_t clubos_close_window(int64_t handle) {
    clubos::close_window(handle);
    return CLUBOS_OK;
}

CLUBOS_API int32_t clubos_notification_state(void) { return clubos::notification_state(); }

// ---------- характеристики ПК ----------
CLUBOS_API int32_t clubos_collect_hardware(clubos_hardware* out) {
    if (out == nullptr) return CLUBOS_ERR_ARGS;
    try {
        const auto info = clubos::collect_hardware(CLUBOS_MAX_DISKS);
        *out = clubos_hardware{};
        copy_text(out->cpu, info.cpu);
        copy_text(out->gpu, info.gpu);
        if (info.network) {
            copy_text(out->ip, info.network->ip);
            copy_text(out->mac, info.network->mac);
        }
        out->ram_gb = info.ram_gb;
        out->disk_count = static_cast<int32_t>(info.disks.size());
        for (std::size_t i = 0; i < info.disks.size(); ++i) {
            copy_text(out->disks[i].name, info.disks[i].name);
            out->disks[i].total_gb = info.disks[i].total_gb;
            out->disks[i].free_gb = info.disks[i].free_gb;
        }
        return CLUBOS_OK;
    } catch (...) {
        return CLUBOS_ERR_INTERNAL;
    }
}

CLUBOS_API int32_t clubos_network(wchar_t ip[48], wchar_t mac[24]) {
    if (ip == nullptr || mac == nullptr) return 0;
    try {
        const auto info = clubos::network_info();
        std::wstring address, hardware;
        if (info) {
            address = info->ip;
            hardware = info->mac;
        }
        std::copy_n(address.c_str(), std::min<std::size_t>(address.size(), 47), ip);
        ip[std::min<std::size_t>(address.size(), 47)] = L'\0';
        std::copy_n(hardware.c_str(), std::min<std::size_t>(hardware.size(), 23), mac);
        mac[std::min<std::size_t>(hardware.size(), 23)] = L'\0';
        return info ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

}  // extern "C"
