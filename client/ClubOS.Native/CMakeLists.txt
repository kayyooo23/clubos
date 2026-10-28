cmake_minimum_required(VERSION 3.20)
project(ClubOSNative VERSION 1.0.0 LANGUAGES CXX)

if(NOT WIN32)
  message(FATAL_ERROR "ClubOS.Native собирается только под Windows")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
# Среда выполнения Visual C++ вшивается в DLL: на клубных ПК ничего доустанавливать не нужно.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
# Готовые файлы (DLL) складываются сюда: оттуда их берут служба и скрипты публикации.
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_CURRENT_SOURCE_DIR}/out")
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY_DEBUG "${CMAKE_CURRENT_SOURCE_DIR}/out/debug")

set(CLUBOS_SOURCES src/block_hasher.cpp src/kiosk_guard.cpp src/windows_info.cpp src/hardware.cpp src/exports.cpp)
set(CLUBOS_LIBS bcrypt user32 advapi32 dwmapi shell32 iphlpapi ws2_32)

add_library(clubos_native SHARED ${CLUBOS_SOURCES})
target_include_directories(clubos_native PUBLIC include PRIVATE src)
target_compile_definitions(clubos_native PRIVATE CLUBOS_NATIVE_BUILD UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
target_link_libraries(clubos_native PRIVATE ${CLUBOS_LIBS})
if(MSVC)
  target_compile_options(clubos_native PRIVATE /W4 /WX /permissive- /utf-8 /guard:cf)
  target_link_options(clubos_native PRIVATE /guard:cf /DYNAMICBASE /NXCOMPAT)
endif()

option(CLUBOS_BUILD_TESTS "Собирать тесты (GoogleTest, скачивается при настройке)" ON)
if(CLUBOS_BUILD_TESTS)
  enable_testing()
  include(FetchContent)
  # GoogleTest: лицензия BSD-3-Clause, см. THIRD-PARTY.md
  FetchContent_Declare(googletest URL https://github.com/google/googletest/archive/refs/tags/v1.15.2.zip DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
  set(gtest_force_shared_crt OFF CACHE BOOL "" FORCE)
  set(INSTALL_GTEST OFF CACHE BOOL "" FORCE)
  FetchContent_MakeAvailable(googletest)

  add_executable(clubos_native_tests tests/block_hasher_tests.cpp tests/exports_tests.cpp tests/kiosk_tests.cpp tests/windows_tests.cpp
                 tests/hardware_tests.cpp ${CLUBOS_SOURCES})
  target_include_directories(clubos_native_tests PRIVATE include src)
  target_compile_definitions(clubos_native_tests PRIVATE CLUBOS_NATIVE_BUILD UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
  target_link_libraries(clubos_native_tests PRIVATE GTest::gtest_main ${CLUBOS_LIBS})
  if(MSVC)
    target_compile_options(clubos_native_tests PRIVATE /W4 /permissive- /utf-8)
  endif()
  # тесты кладём отдельно от рабочих файлов: в out\ остаётся только DLL
  set_target_properties(clubos_native_tests PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_BINARY_DIR}/tests"
    RUNTIME_OUTPUT_DIRECTORY_DEBUG "${CMAKE_BINARY_DIR}/tests")
  # один запуск на весь набор: автообнаружение тестов (gtest_discover_tests) не переносит кириллицу в пути профиля Windows
  add_test(NAME clubos_native_tests COMMAND clubos_native_tests)
endif()
