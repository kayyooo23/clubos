#include "block_hasher.hpp"

#include <algorithm>
#include <new>
#include <vector>

namespace clubos {
namespace {

// Читаем кусками по 1 МиБ, а не блоком целиком: блок может быть до 64 МиБ, а память под него держать незачем.
constexpr std::size_t kChunkSize = 1u << 20;

class FileHandle {
public:
    explicit FileHandle(HANDLE handle) noexcept : handle_(handle) {}
    FileHandle(const FileHandle&) = delete;
    FileHandle& operator=(const FileHandle&) = delete;
    ~FileHandle() {
        if (valid()) CloseHandle(handle_);
    }
    bool valid() const noexcept { return handle_ != INVALID_HANDLE_VALUE; }
    HANDLE get() const noexcept { return handle_; }

private:
    HANDLE handle_;
};

// Приставка \\?\ снимает ограничение в 260 знаков: .NET длинные пути понимает сам, и нативная часть должна вести себя так же.
std::wstring with_long_path_prefix(const std::wstring& path) {
    if (path.rfind(L"\\\\?\\", 0) == 0 || path.rfind(L"\\\\.\\", 0) == 0) return path;
    const DWORD needed = GetFullPathNameW(path.c_str(), 0, nullptr, nullptr);
    if (needed == 0) return path;
    std::wstring full(needed, L'\0');
    const DWORD written = GetFullPathNameW(path.c_str(), needed, full.data(), nullptr);
    if (written == 0 || written >= needed) return path;
    full.resize(written);
    if (full.rfind(L"\\\\", 0) == 0) return L"\\\\?\\UNC\\" + full.substr(2);
    return L"\\\\?\\" + full;
}

bool size_of(HANDLE file, std::int64_t& size) noexcept {
    LARGE_INTEGER value{};
    if (!GetFileSizeEx(file, &value)) return false;
    size = value.QuadPart;
    return true;
}

bool cancelled(const volatile std::int32_t* flag) noexcept { return flag != nullptr && *flag != 0; }

}  // namespace

HashResult hash_file_blocks(const std::wstring& path, std::int64_t expected_length, std::int32_t block_size, std::span<std::uint8_t> out,
                            const volatile std::int32_t* cancel) noexcept {
    try {
        if (path.empty() || block_size <= 0 || expected_length < 0) return {HashStatus::BadArguments, 0};
        const std::int64_t blocks = block_count(expected_length, block_size);
        if (static_cast<std::uint64_t>(blocks) * kSha256Size > out.size()) return {HashStatus::BadArguments, 0};
        if (cancelled(cancel)) return {HashStatus::Cancelled, 0};

        // Читаем без блокировки (FILE_SHARE_*): Steam или другая программа может держать файл открытым.
        FileHandle file(CreateFileW(with_long_path_prefix(path).c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                    nullptr, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, nullptr));
        if (!file.valid()) return {HashStatus::OpenFailed, GetLastError()};

        std::int64_t actual = 0;
        if (!size_of(file.get(), actual)) return {HashStatus::ReadFailed, GetLastError()};
        if (actual != expected_length) return {HashStatus::FileChanged, 0};

        Sha256 sha;
        std::vector<std::uint8_t> buffer(std::min<std::size_t>(kChunkSize, static_cast<std::size_t>(block_size)));
        for (std::int64_t block = 0; block < blocks; ++block) {
            std::int64_t remaining = std::min<std::int64_t>(block_size, expected_length - block * block_size);
            while (remaining > 0) {
                if (cancelled(cancel)) return {HashStatus::Cancelled, 0};
                const auto want = static_cast<DWORD>(std::min<std::int64_t>(static_cast<std::int64_t>(buffer.size()), remaining));
                DWORD got = 0;
                if (!ReadFile(file.get(), buffer.data(), want, &got, nullptr)) return {HashStatus::ReadFailed, GetLastError()};
                if (got == 0) return {HashStatus::FileChanged, 0};  // файл стал короче, чем в начале
                sha.update(std::span<const std::uint8_t>(buffer.data(), got));
                remaining -= got;
            }
            const Digest digest = sha.finish();
            std::copy(digest.begin(), digest.end(), out.begin() + static_cast<std::ptrdiff_t>(block * static_cast<std::int64_t>(kSha256Size)));
        }

        // Файл мог дорасти во время чтения: тогда посчитанное уже не отражает его целиком.
        if (!size_of(file.get(), actual)) return {HashStatus::ReadFailed, GetLastError()};
        if (actual != expected_length) return {HashStatus::FileChanged, 0};
        return {HashStatus::Ok, 0};
    } catch (const CryptoError&) {
        return {HashStatus::CryptoFailed, 0};
    } catch (...) {  // в том числе std::bad_alloc: исключения через границу DLL не выпускаем
        return {HashStatus::Internal, 0};
    }
}

}  // namespace clubos
