cmake_minimum_required(VERSION 3.20)

project(PowerMenuGuard LANGUAGES CXX)

add_executable(PowerMenuGuard WIN32
    src/main.cpp
)

target_compile_features(PowerMenuGuard PRIVATE cxx_std_17)
target_compile_definitions(PowerMenuGuard PRIVATE UNICODE _UNICODE)
target_link_libraries(PowerMenuGuard PRIVATE advapi32 netapi32 shell32 user32 gdi32 comctl32)

if(MINGW)
    target_link_options(PowerMenuGuard PRIVATE -municode)
    target_link_options(PowerMenuGuard PRIVATE -static -static-libgcc -static-libstdc++)
endif()
