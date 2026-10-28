// Подсчёт SHA-256 каждого блока файла: то, что нужно синхронизации игр между ПК клуба (список файлов с хешами блоков).
#pragma once

#include <cstdint>
#include <span>
#include <string>

#include "sha256.hpp"

namespace clubos {

enum class HashStatus : std::int32_t {
    Ok = 0,
    OpenFailed = 1,
    ReadFailed = 2,
    FileChanged = 3,
    BadArguments = 4,
    Cancelled = 5,
    CryptoFailed = 6,
    Internal = 7,
};

struct HashResult {
    HashStatus status = HashStatus::Ok;
    std::uint32_t os_error = 0;  // код GetLastError для OpenFailed и ReadFailed
};

// Сколько блоков в файле длиной length при размере блока block_size (последний может быть короче). Для пустого файла 0.
constexpr std::int64_t block_count(std::int64_t length, std::int32_t block_size) noexcept {
    return (length + block_size - 1) / block_size;
}

// Считает хеши блоков файла и кладёт их подряд в out (по kSha256Size байт на блок); out должен вмещать block_count * kSha256Size байт.
// cancel (может быть nullptr) проверяется между кусками чтения: не ноль значит «остановиться».
HashResult hash_file_blocks(const std::wstring& path, std::int64_t expected_length, std::int32_t block_size, std::span<std::uint8_t> out,
                            const volatile std::int32_t* cancel) noexcept;

}  // namespace clubos
