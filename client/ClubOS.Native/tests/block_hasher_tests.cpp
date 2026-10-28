#include "block_hasher.hpp"

#include <algorithm>
#include <atomic>
#include <functional>
#include <thread>

#include "test_support.hpp"

using namespace clubos;
using namespace clubos::testing;

namespace {

constexpr std::int32_t kMiB = 1 << 20;

struct Hashed {
    HashResult result;
    std::vector<std::string> blocks;  // хеши блоков в шестнадцатеричном виде
};

Hashed hash(const std::filesystem::path& file, std::int64_t length, std::int32_t block_size, const volatile std::int32_t* cancel = nullptr) {
    std::vector<std::uint8_t> out(static_cast<std::size_t>(block_count(length, block_size)) * kSha256Size);
    Hashed hashed{hash_file_blocks(file.wstring(), length, block_size, out, cancel), {}};
    for (std::size_t i = 0; i + kSha256Size <= out.size(); i += kSha256Size) hashed.blocks.push_back(hex(out.data() + i, kSha256Size));
    return hashed;
}

}  // namespace

TEST(BlockCount, RoundsUpAndEmptyFileHasNoBlocks) {
    EXPECT_EQ(block_count(0, 1024), 0);
    EXPECT_EQ(block_count(1, 1024), 1);
    EXPECT_EQ(block_count(1024, 1024), 1);
    EXPECT_EQ(block_count(1025, 1024), 2);
    EXPECT_EQ(block_count(3 * 1024 + 5, 1024), 4);
}

TEST(BlockHasher, MatchesTheStandardTestVectors) {
    TempDir dir;
    const auto abc = write_file(dir.path() / "abc.bin", bytes_of("abc"));
    auto r = hash(abc, 3, 4 * kMiB);
    ASSERT_EQ(r.result.status, HashStatus::Ok);
    ASSERT_EQ(r.blocks.size(), 1u);
    EXPECT_EQ(r.blocks[0], "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");

    // миллион букв «a»: вектор из стандарта FIPS 180-4
    const auto million = write_file(dir.path() / "a.bin", std::vector<std::uint8_t>(1'000'000, 'a'));
    r = hash(million, 1'000'000, 1'000'000);
    ASSERT_EQ(r.result.status, HashStatus::Ok);
    ASSERT_EQ(r.blocks.size(), 1u);
    EXPECT_EQ(r.blocks[0], "cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0");
}

TEST(BlockHasher, AnEmptyFileHasNoBlocks) {
    TempDir dir;
    const auto empty = write_file(dir.path() / "empty.bin", {});
    const auto r = hash(empty, 0, 4 * kMiB);
    EXPECT_EQ(r.result.status, HashStatus::Ok);
    EXPECT_TRUE(r.blocks.empty());
}

TEST(BlockHasher, SplitsIntoBlocksAndTheLastOneIsShorter) {
    TempDir dir;
    const auto data = pattern(3 * 1024 + 5);
    const auto file = write_file(dir.path() / "f.bin", data);
    const auto r = hash(file, static_cast<std::int64_t>(data.size()), 1024);
    ASSERT_EQ(r.result.status, HashStatus::Ok);
    ASSERT_EQ(r.blocks.size(), 4u);
    for (std::size_t i = 0; i < 4; ++i) {
        const auto from = data.begin() + static_cast<std::ptrdiff_t>(i * 1024);
        const auto to = (i == 3) ? data.end() : from + 1024;
        EXPECT_EQ(r.blocks[i], sha256_hex({from, to})) << "блок " << i;
    }
}

TEST(BlockHasher, AMultiChunkBlockEqualsTheOneShotHash) {
    // блок 3 МиБ читается тремя кусками по 1 МиБ: результат должен совпасть с хешем всех байтов разом
    TempDir dir;
    const auto data = pattern(3 * kMiB);
    const auto file = write_file(dir.path() / "big.bin", data);
    const auto r = hash(file, static_cast<std::int64_t>(data.size()), 4 * kMiB);
    ASSERT_EQ(r.result.status, HashStatus::Ok);
    ASSERT_EQ(r.blocks.size(), 1u);
    EXPECT_EQ(r.blocks[0], sha256_hex(data));
}

TEST(BlockHasher, EveryBoundaryAroundTheBlockSizeGivesTheRightBlockCount) {
    TempDir dir;
    for (const std::size_t size : {1u, 1023u, 1024u, 1025u, 2048u, 2049u}) {
        const auto data = pattern(size);
        const auto file = write_file(dir.path() / "edge.bin", data);
        const auto r = hash(file, static_cast<std::int64_t>(size), 1024);
        ASSERT_EQ(r.result.status, HashStatus::Ok) << size;
        EXPECT_EQ(r.blocks.size(), (size + 1023) / 1024) << size;
        EXPECT_EQ(r.blocks.front(), sha256_hex({data.begin(), data.begin() + static_cast<std::ptrdiff_t>(std::min<std::size_t>(size, 1024))})) << size;
    }
}

TEST(BlockHasher, ReadsFilesWithNonAsciiNames) {
    TempDir dir;
    const auto file = write_file(dir.path() / L"игра № 1 (чужая).bin", bytes_of("abc"));
    const auto r = hash(file, 3, 1024);
    ASSERT_EQ(r.result.status, HashStatus::Ok);
    EXPECT_EQ(r.blocks[0], "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
}

TEST(BlockHasher, ReadsFilesBeyondTheClassicPathLimit) {
    TempDir dir;
    auto deep = dir.path();
    while (deep.wstring().size() < 300) deep /= std::wstring(40, L'd');
    const std::wstring prefix = L"\\\\?\\";
    std::filesystem::create_directories(prefix + deep.wstring());
    const auto file = write_file(prefix + (deep / "long.bin").wstring(), bytes_of("abc"));
    const auto r = hash(deep / "long.bin", 3, 1024);
    ASSERT_EQ(r.result.status, HashStatus::Ok) << "путь длиной " << (deep / "long.bin").wstring().size();
    EXPECT_EQ(r.blocks[0], "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    (void)file;
}

TEST(BlockHasher, AMissingFileReportsTheSystemError) {
    TempDir dir;
    const auto r = hash(dir.path() / "nope.bin", 10, 1024);
    EXPECT_EQ(r.result.status, HashStatus::OpenFailed);
    EXPECT_EQ(r.result.os_error, static_cast<std::uint32_t>(ERROR_FILE_NOT_FOUND));
}

TEST(BlockHasher, ADifferentLengthThanExpectedMeansTheFileChanged) {
    TempDir dir;
    const auto file = write_file(dir.path() / "f.bin", pattern(2000));
    EXPECT_EQ(hash(file, 1999, 1024).result.status, HashStatus::FileChanged);  // файл длиннее ожидаемого
    EXPECT_EQ(hash(file, 2001, 1024).result.status, HashStatus::FileChanged);  // файл короче ожидаемого
    EXPECT_EQ(hash(file, 2000, 1024).result.status, HashStatus::Ok);
}

TEST(BlockHasher, BadArgumentsAreRejected) {
    TempDir dir;
    const auto file = write_file(dir.path() / "f.bin", pattern(100));
    std::vector<std::uint8_t> out(kSha256Size);
    EXPECT_EQ(hash_file_blocks(L"", 100, 1024, out, nullptr).status, HashStatus::BadArguments);                   // пустой путь
    EXPECT_EQ(hash_file_blocks(file.wstring(), 100, 0, out, nullptr).status, HashStatus::BadArguments);           // блок нулевой длины
    EXPECT_EQ(hash_file_blocks(file.wstring(), 100, -5, out, nullptr).status, HashStatus::BadArguments);
    EXPECT_EQ(hash_file_blocks(file.wstring(), -1, 1024, out, nullptr).status, HashStatus::BadArguments);         // отрицательный размер
    EXPECT_EQ(hash_file_blocks(file.wstring(), 2048, 1024, out, nullptr).status, HashStatus::BadArguments);       // два блока в буфер на один
}

TEST(BlockHasher, ACancelFlagStopsTheWork) {
    TempDir dir;
    const auto file = write_file(dir.path() / "f.bin", pattern(2 * kMiB));
    const volatile std::int32_t stop = 1;
    EXPECT_EQ(hash(file, 2 * kMiB, kMiB, &stop).result.status, HashStatus::Cancelled);
    const volatile std::int32_t go = 0;
    EXPECT_EQ(hash(file, 2 * kMiB, kMiB, &go).result.status, HashStatus::Ok);
}

TEST(BlockHasher, AFileOpenForWritingByAnotherProgramCanStillBeRead) {
    // так Steam держит файлы игры во время обновления: читать их надо без блокировки
    TempDir dir;
    const auto data = bytes_of("abc");
    const auto file = write_file(dir.path() / "busy.bin", data);
    HANDLE writer = CreateFileW(file.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                                FILE_ATTRIBUTE_NORMAL, nullptr);
    ASSERT_NE(writer, INVALID_HANDLE_VALUE);
    const auto r = hash(file, 3, 1024);
    CloseHandle(writer);
    ASSERT_EQ(r.result.status, HashStatus::Ok);
    EXPECT_EQ(r.blocks[0], "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
}

TEST(BlockHasher, SeveralFilesAtOnceFromDifferentThreadsDoNotMixResults) {
    // служба считает два файла параллельно: у каждого вызова своё состояние, общего нет
    TempDir dir;
    const auto a = pattern(3 * kMiB), b = pattern(3 * kMiB + 7);
    const auto fa = write_file(dir.path() / "a.bin", a), fb = write_file(dir.path() / "b.bin", b);
    std::atomic<int> bad{0};
    auto work = [&](const std::filesystem::path& file, const std::vector<std::uint8_t>& data) {
        for (int i = 0; i < 20; ++i) {
            const auto r = hash(file, static_cast<std::int64_t>(data.size()), kMiB);
            if (r.result.status != HashStatus::Ok || r.blocks.size() != static_cast<std::size_t>(block_count(static_cast<std::int64_t>(data.size()), kMiB)) ||
                r.blocks[0] != sha256_hex({data.begin(), data.begin() + kMiB}))
                ++bad;
        }
    };
    std::thread t1(work, fa, std::cref(a)), t2(work, fb, std::cref(b));
    t1.join();
    t2.join();
    EXPECT_EQ(bad.load(), 0);
}
