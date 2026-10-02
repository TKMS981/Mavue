# Mavue.Shell.Native: "Mavue Quick View" for the Windows 11 File Explorer context menu (IExplorerCommand).
# Built with the MSVC Build Tools (docs/BUILD.md §2.2) by tools/build-native.ps1; not part of Mavue.slnx.
cmake_minimum_required(VERSION 3.21)
project(Mavue.Shell.Native LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>") # static CRT: no VC++ redistributable needed

add_library(Mavue.Shell.Native SHARED
    src/dllmain.cpp
    src/ExplorerCommand.cpp
    src/ExplorerCommand.h
    src/Mavue.Shell.Native.def
    src/Mavue.Shell.Native.rc)

target_compile_definitions(Mavue.Shell.Native PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX _WIN32_WINNT=0x0A00)
target_compile_options(Mavue.Shell.Native PRIVATE /W4 /WX /permissive- /guard:cf /sdl /utf-8)
target_link_options(Mavue.Shell.Native PRIVATE /guard:cf /DYNAMICBASE /NXCOMPAT)
if(CMAKE_SYSTEM_PROCESSOR MATCHES "AMD64|x86_64")
    target_link_options(Mavue.Shell.Native PRIVATE /CETCOMPAT)
endif()
target_link_libraries(Mavue.Shell.Native PRIVATE shlwapi shell32 ole32 oleaut32 uuid)
