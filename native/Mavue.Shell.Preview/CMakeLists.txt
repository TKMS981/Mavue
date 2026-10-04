# Mavue.Shell.Preview: File Explorer preview handler (preview pane) and thumbnail provider (IPreviewHandler,
# IThumbnailProvider). In-process COM server loaded by Explorer's hosts (prevhost.exe / the isolated thumbnail
# dllhost.exe), so it is native: no .NET runtime is loaded into those shared processes.
# Built with the MSVC Build Tools (docs/BUILD.md §2.2) by tools/build-native.ps1; not part of Mavue.slnx.
cmake_minimum_required(VERSION 3.21)
project(Mavue.Shell.Preview LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>") # static CRT: no VC++ redistributable needed

# PDFium headers from the same NuGet package as the pdfium.dll Mavue ships (bblanchon.PDFium.Win32). The DLL is
# loaded at run time from this DLL's folder (no import library), so a missing pdfium.dll only disables PDF.
set(PDFIUM_INCLUDE_DIR "" CACHE PATH "include/ folder of bblanchon.PDFium.Win32 (build/native/include)")
if(NOT EXISTS "${PDFIUM_INCLUDE_DIR}/pdfium/fpdfview.h")
    message(FATAL_ERROR "PDFIUM_INCLUDE_DIR must point to build/native/include of bblanchon.PDFium.Win32 (got '${PDFIUM_INCLUDE_DIR}')")
endif()

add_library(Mavue.Shell.Preview SHARED
    src/dllmain.cpp
    src/Module.cpp
    src/Module.h
    src/Pdfium.cpp
    src/Pdfium.h
    src/Imaging.cpp
    src/Imaging.h
    src/ThumbnailProvider.cpp
    src/ThumbnailProvider.h
    src/PreviewHandler.cpp
    src/PreviewHandler.h
    src/PreviewContent.cpp
    src/PreviewContent.h
    src/MediaPlayer.cpp
    src/MediaPlayer.h
    src/Resources.h
    src/Mavue.Shell.Preview.def
    src/Mavue.Shell.Preview.rc)

target_include_directories(Mavue.Shell.Preview PRIVATE "${PDFIUM_INCLUDE_DIR}/pdfium")
target_compile_definitions(Mavue.Shell.Preview PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX _WIN32_WINNT=0x0A00)
target_compile_options(Mavue.Shell.Preview PRIVATE /W4 /WX /permissive- /guard:cf /sdl /utf-8 /EHsc)
target_link_options(Mavue.Shell.Preview PRIVATE /guard:cf /DYNAMICBASE /NXCOMPAT)
if(CMAKE_SYSTEM_PROCESSOR MATCHES "AMD64|x86_64")
    target_link_options(Mavue.Shell.Preview PRIVATE /CETCOMPAT)
endif()
target_link_libraries(Mavue.Shell.Preview PRIVATE
    shlwapi shell32 ole32 oleaut32 uuid user32 gdi32 windowscodecs d2d1 d3d11 dxgi dwrite mfplat mfuuid propsys)
