#include "windows_info.hpp"

#include <map>

#include <windows.h>
#include <dwmapi.h>
#include <shellapi.h>
#include <shlobj.h>

namespace clubos {
namespace {

constexpr int kDwmaCloaked = 14;  // DWMWA_CLOAKED
constexpr std::int32_t kAcceptsNotifications = 5;  // QUNS_ACCEPTS_NOTIFICATIONS

class ProcessHandle {
public:
    explicit ProcessHandle(HANDLE handle) noexcept : handle_(handle) {}
    ProcessHandle(const ProcessHandle&) = delete;
    ProcessHandle& operator=(const ProcessHandle&) = delete;
    ~ProcessHandle() {
        if (handle_ != nullptr) CloseHandle(handle_);
    }
    HANDLE get() const noexcept { return handle_; }

private:
    HANDLE handle_;
};

std::wstring class_name_of(HWND hwnd) {
    wchar_t buffer[128] = {};
    const int length = GetClassNameW(hwnd, buffer, static_cast<int>(std::size(buffer)));
    return std::wstring(buffer, static_cast<std::size_t>(length > 0 ? length : 0));
}

WindowInfo describe(HWND hwnd, std::map<std::uint32_t, std::wstring>& names) {
    WindowInfo info;
    info.handle = reinterpret_cast<std::int64_t>(hwnd);
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    info.pid = static_cast<std::int32_t>(pid);
    auto cached = names.find(pid);
    if (cached == names.end()) cached = names.emplace(pid, process_name_of(pid)).first;
    info.process_name = cached->second;
    info.class_name = class_name_of(hwnd);
    RECT rect{};
    if (GetWindowRect(hwnd, &rect)) {
        info.width = rect.right - rect.left;
        info.height = rect.bottom - rect.top;
    }
    info.minimized = IsIconic(hwnd) != FALSE;
    return info;
}

struct EnumState {
    std::vector<WindowInfo> windows;
    std::map<std::uint32_t, std::wstring> names;
};

BOOL CALLBACK collect_window(HWND hwnd, LPARAM lparam) {
    auto& state = *reinterpret_cast<EnumState*>(lparam);
    try {
        if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != nullptr) return TRUE;
        if ((GetWindowLongW(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return TRUE;
        int cloaked = 0;
        if (SUCCEEDED(DwmGetWindowAttribute(hwnd, kDwmaCloaked, &cloaked, sizeof cloaked)) && cloaked != 0) return TRUE;
        state.windows.push_back(describe(hwnd, state.names));
    } catch (...) {
        return FALSE;  // не хватило памяти: останавливаем перебор, вызывающий получит то, что успело собраться
    }
    return TRUE;
}

}  // namespace

std::wstring process_name_of(std::uint32_t pid) {
    if (pid == 0) return {};
    const ProcessHandle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid));
    if (process.get() == nullptr) return {};
    wchar_t path[MAX_PATH * 2] = {};
    DWORD size = static_cast<DWORD>(std::size(path));
    if (!QueryFullProcessImageNameW(process.get(), 0, path, &size)) return {};
    std::wstring name(path, size);
    if (const auto slash = name.find_last_of(L"\\/"); slash != std::wstring::npos) name.erase(0, slash + 1);
    if (const auto dot = name.find_last_of(L'.'); dot != std::wstring::npos && dot > 0) name.erase(dot);
    return name;
}

std::vector<WindowInfo> top_level_windows() {
    EnumState state;
    EnumWindows(collect_window, reinterpret_cast<LPARAM>(&state));
    return std::move(state.windows);
}

std::optional<WindowInfo> foreground_window() {
    HWND hwnd = GetForegroundWindow();
    if (hwnd == nullptr) return std::nullopt;
    std::map<std::uint32_t, std::wstring> names;
    return describe(hwnd, names);
}

// Windows не даёт фоновому процессу забрать фокус, поэтому на время вызова наш поток присоединяется к потоку окна в фокусе:
// так делают все программы, которым нужно выйти поверх игры.
void bring_to_front(std::int64_t handle) noexcept {
    const auto hwnd = reinterpret_cast<HWND>(handle);
    if (!IsWindow(hwnd)) return;
    if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
    const DWORD foreground_thread = GetWindowThreadProcessId(GetForegroundWindow(), nullptr);
    const DWORD ours = GetCurrentThreadId();
    const bool attached = foreground_thread != 0 && foreground_thread != ours && AttachThreadInput(ours, foreground_thread, TRUE);
    BringWindowToTop(hwnd);
    SetForegroundWindow(hwnd);
    if (attached) AttachThreadInput(ours, foreground_thread, FALSE);
}

void close_window(std::int64_t handle) noexcept { PostMessageW(reinterpret_cast<HWND>(handle), WM_CLOSE, 0, 0); }

std::int32_t notification_state() noexcept {
    QUERY_USER_NOTIFICATION_STATE state{};
    return SUCCEEDED(SHQueryUserNotificationState(&state)) ? static_cast<std::int32_t>(state) : kAcceptsNotifications;
}

}  // namespace clubos
