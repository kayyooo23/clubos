/*
 * ClubOS.Native: нативная часть клиента (C++20, только Windows).
 *
 * Публичный интерфейс на чистом C: его вызывают служба ClubOS.Service и оверлей ClubOS.Overlay на C# через P/Invoke.
 * Исключения C++ через границу DLL не выходят: каждая функция возвращает код результата или безопасное значение.
 *
 * Что внутри: подсчёт хешей блоков файла (синхронизация игр), перехват клавиш киоска, опрос окон и процессов для оболочки гостя,
 * сбор характеристик ПК.
 */
#ifndef CLUBOS_NATIVE_H
#define CLUBOS_NATIVE_H

#include <stdint.h>
#include <wchar.h>

#ifdef _WIN32
#  ifdef CLUBOS_NATIVE_BUILD
#    define CLUBOS_API __declspec(dllexport)
#  else
#    define CLUBOS_API __declspec(dllimport)
#  endif
#  define CLUBOS_CALL __cdecl
#else
#  define CLUBOS_API
#  define CLUBOS_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Версия интерфейса: C# сверяет её при загрузке, чтобы не вызвать несовместимую библиотеку. */
#define CLUBOS_ABI_VERSION 2

CLUBOS_API int32_t clubos_abi_version(void);

/* ======================= подсчёт хешей блоков файла ======================= */

/* Размер одного хеша SHA-256 в байтах. */
#define CLUBOS_SHA256_SIZE 32

/* Коды результата. */
#define CLUBOS_OK 0
#define CLUBOS_ERR_OPEN 1       /* файл не открылся (подробности в *os_error) */
#define CLUBOS_ERR_READ 2       /* ошибка чтения (подробности в *os_error) */
#define CLUBOS_ERR_CHANGED 3    /* файл изменился во время подсчёта: размер не тот, что ожидался */
#define CLUBOS_ERR_ARGS 4       /* неверные аргументы, в том числе мал буфер результата */
#define CLUBOS_ERR_CANCELLED 5  /* вызывающий поднял флаг отмены */
#define CLUBOS_ERR_CRYPTO 6     /* системная криптография недоступна */
#define CLUBOS_ERR_INTERNAL 7   /* непредвиденная ошибка (например, не хватило памяти) */

/*
 * Считает SHA-256 каждого блока файла: блоки по block_size байт, последний может быть короче.
 *
 *   path             путь к файлу (UTF-16, длинные пути поддерживаются);
 *   expected_length  размер файла, который ждёт вызывающий: если на деле другой, результат CLUBOS_ERR_CHANGED;
 *   block_size       размер блока в байтах, больше нуля;
 *   out_digests      буфер под результат: на каждый блок по 32 байта подряд;
 *   out_capacity     размер буфера в байтах: должен быть не меньше блоков * 32;
 *   cancel           необязательный флаг отмены (может быть NULL): не ноль значит «остановись»;
 *   os_error         необязательно (может быть NULL): код ошибки Windows при CLUBOS_ERR_OPEN и CLUBOS_ERR_READ.
 *
 * Файл читается без блокировки (другим процессам, например Steam, не мешает писать в него).
 */
CLUBOS_API int32_t clubos_hash_blocks(const wchar_t* path, int64_t expected_length, int32_t block_size, uint8_t* out_digests,
                                      int64_t out_capacity, const volatile int32_t* cancel, uint32_t* os_error);

/* ======================= перехват клавиш киоска ======================= */

/* Набор перехватываемых клавиш. Lock: пока виден экран блокировки, закрыто всё, чем гость мог бы уйти (Win, Alt+Tab, Alt+Esc, Ctrl+Esc,
 * Ctrl+Shift+Esc, Alt+F4). Session: пока гость играет, закрыты только Win, Ctrl+Esc и Ctrl+Shift+Esc; Alt+Tab и Alt+F4 нужны игре. */
#define CLUBOS_KIOSK_LOCK 0
#define CLUBOS_KIOSK_SESSION 1

/* Вызывается, когда нажато Ctrl+Shift+F12 три раза за 3 секунды (только если разрешён аварийный выход). Вызывается в потоке, который включил перехват. */
typedef void(CLUBOS_CALL* clubos_exit_callback)(void);

/*
 * Включает перехват клавиш (низкоуровневый хук клавиатуры) для процесса. Вызывать из потока с циклом сообщений (у WPF это главный поток):
 * хук работает в нём. Уже включён: только меняет набор клавиш и настройки. Возвращает CLUBOS_OK или CLUBOS_ERR_OPEN (хук не встал, *os_error).
 */
CLUBOS_API int32_t clubos_kiosk_enable(int32_t profile, int32_t allow_exit, clubos_exit_callback on_exit, uint32_t* os_error);
CLUBOS_API void clubos_kiosk_set_profile(int32_t profile);
CLUBOS_API void clubos_kiosk_disable(void);
CLUBOS_API int32_t clubos_kiosk_active(void);
/* Правило перехвата в одном месте: нужно ли проглотить нажатие (1) или пропустить (0). vk это код клавиши Windows. */
CLUBOS_API int32_t clubos_kiosk_should_block(int32_t vk, int32_t alt, int32_t ctrl, int32_t shift, int32_t profile);

/* ======================= окна и процессы ======================= */

typedef struct clubos_window_info {
    int64_t handle;
    int32_t pid;
    int32_t width;
    int32_t height;
    int32_t minimized;
    wchar_t process_name[64];  /* имя exe без расширения, пусто если процесс закрылся или нет доступа */
    wchar_t class_name[128];
} clubos_window_info;

/*
 * Видимые окна верхнего уровня, самое верхнее первым: без вспомогательных (у них есть владелец), «инструментальных» и скрытых UWP-окон.
 * Заполняет не больше capacity записей; в *count кладёт сколько окон есть на самом деле (может быть больше capacity: тогда вызовите снова с большим буфером).
 */
CLUBOS_API int32_t clubos_list_windows(clubos_window_info* out, int32_t capacity, int32_t* count);
/* Окно в фокусе: 1 и заполненная запись, 0 если фокуса нет. */
CLUBOS_API int32_t clubos_foreground_window(clubos_window_info* out);
/* Вывести окно вперёд и в фокус (из свёрнутого тоже). */
CLUBOS_API int32_t clubos_bring_to_front(int64_t handle);
/* Попросить окно закрыться (как крестик), процесс не убивается. */
CLUBOS_API int32_t clubos_close_window(int64_t handle);
/* Состояние уведомлений Windows (SHQueryUserNotificationState): 2 и 3 полноэкранное приложение, 5 можно показывать уведомления; при сбое 5. */
CLUBOS_API int32_t clubos_notification_state(void);

/* ======================= характеристики ПК ======================= */

#define CLUBOS_MAX_DISKS 16

typedef struct clubos_disk {
    wchar_t name[8];  /* «C:» */
    int32_t total_gb;
    int32_t free_gb;
} clubos_disk;

typedef struct clubos_hardware {
    wchar_t cpu[128];  /* пусто, если определить не вышло */
    wchar_t gpu[128];
    wchar_t ip[48];
    wchar_t mac[24];   /* AA-BB-CC-DD-EE-FF */
    int32_t ram_gb;    /* 0, если определить не вышло */
    int32_t disk_count;
    clubos_disk disks[CLUBOS_MAX_DISKS];
} clubos_hardware;

/* Собирает процессор, память, видеокарту, фиксированные диски и рабочий сетевой адаптер. Каждая часть независима: что не определилось, остаётся пустым. */
CLUBOS_API int32_t clubos_collect_hardware(clubos_hardware* out);
/* Только адрес и MAC рабочего сетевого адаптера: проводной в приоритете, виртуальные (VPN, Hyper-V, TAP) не берутся. 1 если нашёлся. */
CLUBOS_API int32_t clubos_network(wchar_t ip[48], wchar_t mac[24]);

#ifdef __cplusplus
}
#endif

#endif /* CLUBOS_NATIVE_H */
