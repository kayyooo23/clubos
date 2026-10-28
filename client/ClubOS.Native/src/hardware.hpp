// Характеристики ПК средствами самой Windows: процессор (CPUID), память, видеокарта (реестр драйверов), диски, сетевой адаптер.
// Каждая часть независима: что определить не вышло, остаётся пустым.
#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace clubos {

struct DiskInfo {
    std::wstring name;  // «C:»
    std::int32_t total_gb = 0;
    std::int32_t free_gb = 0;
};

struct NetworkInfo {
    std::wstring ip;
    std::wstring mac;  // AA-BB-CC-DD-EE-FF
};

struct HardwareInfo {
    std::wstring cpu;
    std::wstring gpu;
    std::int32_t ram_gb = 0;
    std::vector<DiskInfo> disks;
    std::optional<NetworkInfo> network;
};

// Байты в гигабайты с округлением до ближайшего (как в отчёте сервера), не больше миллиона.
std::int32_t to_gb(std::uint64_t bytes) noexcept;

// «Intel(R) Core(TM) i7-14700K», пробелы подряд схлопнуты; пусто, если процессор не сообщил название.
std::wstring cpu_name();
// Установленная память, ГБ (по данным платы, а не доступная системе); 0 при сбое.
std::int32_t installed_ram_gb() noexcept;
// Видеокарта: из всех адаптеров «настоящая» (NVIDIA, Radeon, GeForce), а не встроенная или базовый драйвер Microsoft.
std::wstring gpu_name();
// Фиксированные диски (не съёмные и не сетевые); не больше max.
std::vector<DiskInfo> fixed_disks(std::size_t max);
// Рабочий адаптер: проводной в приоритете, виртуальные (Virtual, VPN, TAP) не берутся; адреса 169.254.* пропускаются.
std::optional<NetworkInfo> network_info();

HardwareInfo collect_hardware(std::size_t max_disks);

// Выбор видеокарты из названий драйверов: правило вынесено, чтобы проверять его без реестра.
std::wstring choose_gpu(const std::vector<std::wstring>& driver_names);

// Форматирует MAC как AA-BB-CC-DD-EE-FF; пусто, если байтов не шесть.
std::wstring format_mac(const std::uint8_t* bytes, std::size_t size);

}  // namespace clubos
