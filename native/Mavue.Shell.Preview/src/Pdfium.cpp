// PDFium for previews and thumbnails: the pdfium.dll Mavue ships (bblanchon/pdfium-binaries, docs/DEPENDENCIES.md),
// loaded from this DLL's folder by full path at first use. Same settings as Mavue.Pdf (library config version 2,
// annotations drawn, limited image cache, white page).

#include "Pdfium.h"

#include <fpdfview.h>
#include <fpdf_progressive.h>

#include <mutex>
#include <new>

namespace mavue::pdf
{
    namespace
    {
        struct Api
        {
            decltype(&FPDF_InitLibraryWithConfig) InitLibraryWithConfig = nullptr;
            decltype(&FPDF_LoadCustomDocument) LoadCustomDocument = nullptr;
            decltype(&FPDF_CloseDocument) CloseDocument = nullptr;
            decltype(&FPDF_GetLastError) GetLastError = nullptr;
            decltype(&FPDF_GetPageCount) GetPageCount = nullptr;
            decltype(&FPDF_GetPageSizeByIndexF) GetPageSizeByIndexF = nullptr;
            decltype(&FPDF_LoadPage) LoadPage = nullptr;
            decltype(&FPDF_ClosePage) ClosePage = nullptr;
            decltype(&FPDFBitmap_CreateEx) BitmapCreateEx = nullptr;
            decltype(&FPDFBitmap_FillRect) BitmapFillRect = nullptr;
            decltype(&FPDFBitmap_Destroy) BitmapDestroy = nullptr;
            decltype(&FPDF_RenderPageBitmap_Start) RenderStart = nullptr;
            decltype(&FPDF_RenderPage_Continue) RenderContinue = nullptr;
            decltype(&FPDF_RenderPage_Close) RenderClose = nullptr;
        };

        Api g_api;
        bool g_available = false;
        std::once_flag g_once;
        SRWLOCK g_lock = SRWLOCK_INIT; // every PDFium call

        class Lock final
        {
        public:
            Lock() noexcept { AcquireSRWLockExclusive(&g_lock); }
            ~Lock() { ReleaseSRWLockExclusive(&g_lock); }
            Lock(const Lock&) = delete;
            Lock& operator=(const Lock&) = delete;
        };

        template <typename T>
        bool Resolve(HMODULE module, T& target, const char* name)
        {
            target = reinterpret_cast<T>(reinterpret_cast<void*>(GetProcAddress(module, name)));
            return target != nullptr;
        }

        void Load()
        {
            std::wstring directory = ModuleDirectory();
            if (directory.empty())
            {
                return;
            }

            std::wstring path = directory + L"\\pdfium.dll";
            // Full path + LOAD_WITH_ALTERED_SEARCH_PATH: never another pdfium.dll from the host's search path.
            HMODULE module = LoadLibraryExW(path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
            if (module == nullptr)
            {
                Trace("pdfium-missing", "\"error\":" + std::to_string(::GetLastError()));
                return;
            }

            bool ok = Resolve(module, g_api.InitLibraryWithConfig, "FPDF_InitLibraryWithConfig") &&
                Resolve(module, g_api.LoadCustomDocument, "FPDF_LoadCustomDocument") &&
                Resolve(module, g_api.CloseDocument, "FPDF_CloseDocument") &&
                Resolve(module, g_api.GetLastError, "FPDF_GetLastError") &&
                Resolve(module, g_api.GetPageCount, "FPDF_GetPageCount") &&
                Resolve(module, g_api.GetPageSizeByIndexF, "FPDF_GetPageSizeByIndexF") &&
                Resolve(module, g_api.LoadPage, "FPDF_LoadPage") &&
                Resolve(module, g_api.ClosePage, "FPDF_ClosePage") &&
                Resolve(module, g_api.BitmapCreateEx, "FPDFBitmap_CreateEx") &&
                Resolve(module, g_api.BitmapFillRect, "FPDFBitmap_FillRect") &&
                Resolve(module, g_api.BitmapDestroy, "FPDFBitmap_Destroy") &&
                Resolve(module, g_api.RenderStart, "FPDF_RenderPageBitmap_Start") &&
                Resolve(module, g_api.RenderContinue, "FPDF_RenderPage_Continue") &&
                Resolve(module, g_api.RenderClose, "FPDF_RenderPage_Close");
            if (!ok)
            {
                Trace("pdfium-missing", "\"error\":\"exports\"");
                FreeLibrary(module);
                return;
            }

            // Kept loaded for the life of the process (PDFium is initialized once and never torn down, as in Mavue.Pdf).
            Lock lock;
            FPDF_LIBRARY_CONFIG config{};
            config.version = 2; // no user font paths; the binaries have no V8/XFA
            g_api.InitLibraryWithConfig(&config);
            g_available = true;
        }

        struct Pause final : IFSDK_PAUSE
        {
            const std::atomic<bool>* Cancel = nullptr;

            static FPDF_BOOL Check(IFSDK_PAUSE* self)
            {
                const auto* pause = static_cast<Pause*>(self);
                return pause->Cancel != nullptr && pause->Cancel->load() ? 1 : 0;
            }
        };
    }

    struct Document::FileAccess
    {
        FPDF_FILEACCESS Access{};
    };

    bool Available()
    {
        std::call_once(g_once, Load);
        return g_available;
    }

    OpenResult Document::Open(IStream* stream, std::unique_ptr<Document>& document)
    {
        document.reset();
        if (!Available())
        {
            return OpenResult::Unavailable;
        }

        ULONGLONG size = StreamSize(stream);
        if (size == 0)
        {
            return OpenResult::Failed;
        }

        if (size > 0xFFFFFFFFull)
        {
            return OpenResult::TooLarge;
        }

        std::unique_ptr<Document> opened(new (std::nothrow) Document());
        if (!opened)
        {
            return OpenResult::Failed;
        }

        opened->m_stream = stream;
        opened->m_access = std::make_unique<FileAccess>();
        opened->m_access->Access.m_FileLen = static_cast<unsigned long>(size);
        opened->m_access->Access.m_GetBlock = &Document::GetBlock;
        opened->m_access->Access.m_Param = opened.get();

        Lock lock;
        FPDF_DOCUMENT handle = g_api.LoadCustomDocument(&opened->m_access->Access, nullptr);
        if (handle == nullptr)
        {
            return g_api.GetLastError() == FPDF_ERR_PASSWORD ? OpenResult::Password : OpenResult::Failed;
        }

        opened->m_document = handle;
        int count = g_api.GetPageCount(handle);
        if (count <= 0)
        {
            return OpenResult::Failed; // `lock` is released before `opened` closes the document
        }

        count = count > MaxPages ? MaxPages : count;
        opened->m_sizes.resize(static_cast<size_t>(count));
        for (int i = 0; i < count; i++)
        {
            FS_SIZEF pageSize{};
            if (g_api.GetPageSizeByIndexF(handle, i, &pageSize) && pageSize.width > 0 && pageSize.height > 0)
            {
                opened->m_sizes[static_cast<size_t>(i)] = { pageSize.width, pageSize.height };
            }
            else
            {
                opened->m_sizes[static_cast<size_t>(i)] = { 612, 792 }; // a broken page: Letter, drawn blank
            }
        }

        document = std::move(opened);
        return OpenResult::Ok;
    }

    Document::~Document()
    {
        if (m_document != nullptr)
        {
            Lock lock;
            g_api.CloseDocument(static_cast<FPDF_DOCUMENT>(m_document));
        }
    }

    int Document::GetBlock(void* param, unsigned long position, unsigned char* buffer, unsigned long size)
    {
        auto* self = static_cast<Document*>(param);
        LARGE_INTEGER offset{};
        offset.QuadPart = position;
        if (FAILED(self->m_stream->Seek(offset, STREAM_SEEK_SET, nullptr)))
        {
            return 0;
        }

        unsigned long done = 0;
        while (done < size)
        {
            ULONG read = 0;
            HRESULT hr = self->m_stream->Read(buffer + done, size - done, &read);
            if (FAILED(hr) || read == 0)
            {
                return 0;
            }

            done += read;
        }

        return 1;
    }

    bool Document::Render(int index, int width, int height, std::vector<BYTE>& pixels, const std::atomic<bool>* cancel)
    {
        if (index < 0 || index >= PageCount() || width <= 0 || height <= 0 || static_cast<ULONGLONG>(width) * static_cast<ULONGLONG>(height) > 64ull * 1024 * 1024)
        {
            return false;
        }

        pixels.assign(static_cast<size_t>(width) * static_cast<size_t>(height) * 4, 0);
        Lock lock;
        FPDF_PAGE page = g_api.LoadPage(static_cast<FPDF_DOCUMENT>(m_document), index);
        if (page == nullptr)
        {
            return false;
        }

        FPDF_BITMAP bitmap = g_api.BitmapCreateEx(width, height, FPDFBitmap_BGRA, pixels.data(), width * 4);
        bool done = false;
        if (bitmap != nullptr)
        {
            g_api.BitmapFillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
            Pause pause{};
            pause.version = 1;
            pause.NeedToPauseNow = &Pause::Check;
            pause.Cancel = cancel;
            int status = g_api.RenderStart(bitmap, page, 0, 0, width, height, 0, FPDF_ANNOT | FPDF_RENDER_LIMITEDIMAGECACHE, &pause);
            while (status == FPDF_RENDER_TOBECONTINUED && !(cancel != nullptr && cancel->load()))
            {
                status = g_api.RenderContinue(page, &pause);
            }

            done = status == FPDF_RENDER_DONE;
            g_api.RenderClose(page);
            g_api.BitmapDestroy(bitmap);
        }

        g_api.ClosePage(page);
        if (done)
        {
            for (size_t i = 3; i < pixels.size(); i += 4)
            {
                pixels[i] = 0xFF; // PDFium leaves BGRx alpha undefined; the page is opaque
            }
        }

        return done;
    }
}
