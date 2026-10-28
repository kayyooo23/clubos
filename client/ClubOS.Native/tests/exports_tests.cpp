// Проверка того, что видит C#: функции с интерфейсом на C из clubos_native.h.
#include "clubos_native.h"

#include "test_support.hpp"

using namespace clubos::testing;

TEST(Exports, TheAbiVersionMatchesTheHeader) { EXPECT_EQ(clubos_abi_version(), CLUBOS_ABI_VERSION); }

TEST(Exports, HashesBlocksAndFillsTheBufferWithRawDigests) {
    TempDir dir;
    const auto file = write_file(dir.path() / "abc.bin", bytes_of("abc"));
    std::vector<uint8_t> out(CLUBOS_SHA256_SIZE);
    uint32_t os_error = 99;
    ASSERT_EQ(clubos_hash_blocks(file.c_str(), 3, 1024, out.data(), static_cast<int64_t>(out.size()), nullptr, &os_error), CLUBOS_OK);
    EXPECT_EQ(os_error, 0u);
    EXPECT_EQ(hex(out.data(), out.size()), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
}

TEST(Exports, AnEmptyFileNeedsNoBuffer) {
    TempDir dir;
    const auto file = write_file(dir.path() / "empty.bin", {});
    EXPECT_EQ(clubos_hash_blocks(file.c_str(), 0, 1024, nullptr, 0, nullptr, nullptr), CLUBOS_OK);
}

TEST(Exports, ReturnsCodesAndTheSystemErrorInsteadOfThrowing) {
    TempDir dir;
    std::vector<uint8_t> out(CLUBOS_SHA256_SIZE);
    uint32_t os_error = 0;
    const auto missing = (dir.path() / "nope.bin").wstring();
    EXPECT_EQ(clubos_hash_blocks(missing.c_str(), 3, 1024, out.data(), 32, nullptr, &os_error), CLUBOS_ERR_OPEN);
    EXPECT_EQ(os_error, static_cast<uint32_t>(ERROR_FILE_NOT_FOUND));

    const auto file = write_file(dir.path() / "f.bin", bytes_of("abc"));
    EXPECT_EQ(clubos_hash_blocks(file.c_str(), 4, 1024, out.data(), 32, nullptr, nullptr), CLUBOS_ERR_CHANGED);
    EXPECT_EQ(clubos_hash_blocks(nullptr, 3, 1024, out.data(), 32, nullptr, nullptr), CLUBOS_ERR_ARGS);
    EXPECT_EQ(clubos_hash_blocks(file.c_str(), 3, 1024, nullptr, 32, nullptr, nullptr), CLUBOS_ERR_ARGS);
    EXPECT_EQ(clubos_hash_blocks(file.c_str(), 3, 1024, out.data(), -1, nullptr, nullptr), CLUBOS_ERR_ARGS);
    EXPECT_EQ(clubos_hash_blocks(file.c_str(), 3, 1024, out.data(), 16, nullptr, nullptr), CLUBOS_ERR_ARGS);  // буфер мал
}

TEST(Exports, TheCancelFlagIsHonoured) {
    TempDir dir;
    const auto file = write_file(dir.path() / "f.bin", bytes_of("abc"));
    std::vector<uint8_t> out(CLUBOS_SHA256_SIZE);
    volatile int32_t stop = 1;
    EXPECT_EQ(clubos_hash_blocks(file.c_str(), 3, 1024, out.data(), 32, &stop, nullptr), CLUBOS_ERR_CANCELLED);
}
