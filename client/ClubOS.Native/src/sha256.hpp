// Обёртка над SHA-256 из системной криптографии Windows (CNG, bcrypt.dll): своей реализации хеша здесь нет,
// сторонний код не подключается. Объект можно использовать повторно: после finish() он готов считать следующий хеш.
#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <span>
#include <stdexcept>
#include <vector>

#include <windows.h>
#include <bcrypt.h>

namespace clubos {

inline constexpr std::size_t kSha256Size = 32;
using Digest = std::array<std::uint8_t, kSha256Size>;

// Ошибка системной криптографии: наружу (через границу DLL) не выходит, её превращает в код результата экспортируемая функция.
class CryptoError : public std::runtime_error {
public:
    explicit CryptoError(const char* what, NTSTATUS status) : std::runtime_error(what), status_(status) {}
    NTSTATUS status() const noexcept { return status_; }

private:
    NTSTATUS status_;
};

class Sha256 {
public:
    Sha256() {
        check(BCryptOpenAlgorithmProvider(&alg_, BCRYPT_SHA256_ALGORITHM, nullptr, BCRYPT_HASH_REUSABLE_FLAG), "BCryptOpenAlgorithmProvider");
        DWORD object_size = 0, written = 0;
        check(BCryptGetProperty(alg_, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&object_size), sizeof object_size, &written, 0),
              "BCryptGetProperty");
        object_.resize(object_size);
        check(BCryptCreateHash(alg_, &hash_, object_.data(), static_cast<ULONG>(object_.size()), nullptr, 0, BCRYPT_HASH_REUSABLE_FLAG),
              "BCryptCreateHash");
    }

    Sha256(const Sha256&) = delete;
    Sha256& operator=(const Sha256&) = delete;

    ~Sha256() {
        if (hash_) BCryptDestroyHash(hash_);
        if (alg_) BCryptCloseAlgorithmProvider(alg_, 0);
    }

    void update(std::span<const std::uint8_t> data) {
        check(BCryptHashData(hash_, const_cast<PUCHAR>(data.data()), static_cast<ULONG>(data.size()), 0), "BCryptHashData");
    }

    // Завершает хеш и сбрасывает объект для следующего.
    Digest finish() {
        Digest digest{};
        check(BCryptFinishHash(hash_, digest.data(), static_cast<ULONG>(digest.size()), 0), "BCryptFinishHash");
        return digest;
    }

private:
    static void check(NTSTATUS status, const char* what) {
        if (!BCRYPT_SUCCESS(status)) throw CryptoError(what, status);
    }

    BCRYPT_ALG_HANDLE alg_ = nullptr;
    BCRYPT_HASH_HANDLE hash_ = nullptr;
    std::vector<std::uint8_t> object_;
};

}  // namespace clubos
