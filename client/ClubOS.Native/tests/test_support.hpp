// Общее для тестов: временная папка, запись файлов, шестнадцатеричная запись хеша.
#pragma once

#include <cstdint>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

#include <gtest/gtest.h>

#include "sha256.hpp"

namespace clubos::testing {

// Удаляет дерево папок, в том числе с путями длиннее 260 знаков (для них нужна приставка \\?\); ошибки не бросает.
inline void remove_tree(const std::filesystem::path& path) {
    std::error_code ignored;
    std::filesystem::remove_all(std::filesystem::path(L"\\\\?\\" + std::filesystem::absolute(path).wstring()), ignored);
    std::filesystem::remove_all(path, ignored);
}

// Временная папка на время одного теста; удаляется в конце.
class TempDir {
public:
    TempDir() {
        const auto* info = ::testing::UnitTest::GetInstance()->current_test_info();
        path_ = std::filesystem::temp_directory_path() / (std::string("clubos-native-") + info->test_suite_name() + "-" + info->name());
        remove_tree(path_);          // остаток от прошлого запуска, если тот оборвался
        std::filesystem::create_directories(path_);
    }
    TempDir(const TempDir&) = delete;
    TempDir& operator=(const TempDir&) = delete;
    ~TempDir() { remove_tree(path_); }
    const std::filesystem::path& path() const { return path_; }

private:
    std::filesystem::path path_;
};

inline std::filesystem::path write_file(const std::filesystem::path& file, const std::vector<std::uint8_t>& bytes) {
    std::ofstream out(file, std::ios::binary | std::ios::trunc);
    out.write(reinterpret_cast<const char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    return file;
}

inline std::vector<std::uint8_t> bytes_of(const std::string& text) { return {text.begin(), text.end()}; }

// Предсказуемые «случайные» данные: один и тот же размер даёт один и тот же набор байтов.
inline std::vector<std::uint8_t> pattern(std::size_t size) {
    std::vector<std::uint8_t> data(size);
    std::uint32_t state = 2463534242u;
    for (auto& b : data) {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        b = static_cast<std::uint8_t>(state);
    }
    return data;
}

inline std::string hex(const std::uint8_t* data, std::size_t size) {
    static constexpr char kDigits[] = "0123456789abcdef";
    std::string text;
    for (std::size_t i = 0; i < size; ++i) {
        text += kDigits[data[i] >> 4];
        text += kDigits[data[i] & 15];
    }
    return text;
}

// Хеш всех байтов за один вызов системной функции: независимый путь для сверки с потоковым подсчётом.
inline std::string sha256_hex(const std::vector<std::uint8_t>& data) {
    Digest digest{};
    const NTSTATUS status = BCryptHash(BCRYPT_SHA256_ALG_HANDLE, nullptr, 0, const_cast<PUCHAR>(data.data()), static_cast<ULONG>(data.size()),
                                       digest.data(), static_cast<ULONG>(digest.size()));
    EXPECT_TRUE(BCRYPT_SUCCESS(status));
    return hex(digest.data(), digest.size());
}

}  // namespace clubos::testing
