#pragma once

#include "Module.h"

#include <atomic>
#include <memory>
#include <vector>

namespace mavue::pdf
{
    enum class OpenResult
    {
        Ok,
        Password,  // encrypted: not shown (no password prompt in Explorer)
        Failed,    // broken or not a PDF
        TooLarge,  // over 4 GB (PDFium's custom file access is 32-bit)
        Unavailable, // pdfium.dll missing or not loadable
    };

    struct PageSize
    {
        float Width = 0;  // points
        float Height = 0;
    };

    // True when pdfium.dll (the same build Mavue ships, next to this DLL) is loaded and initialized.
    bool Available();

    // A PDF read through an IStream. PDFium is not thread-safe: every call takes one process-wide lock, and the
    // document must be used from one thread at a time (the stream is read inside those calls).
    class Document final
    {
    public:
        static OpenResult Open(IStream* stream, std::unique_ptr<Document>& document);
        ~Document();
        Document(const Document&) = delete;
        Document& operator=(const Document&) = delete;

        int PageCount() const noexcept { return static_cast<int>(m_sizes.size()); }
        PageSize Size(int index) const noexcept { return m_sizes[static_cast<size_t>(index)]; }

        // Draws page `index` into a top-down BGRA buffer of width x height on white (annotations included).
        // Progressive: returns false as soon as `cancel` becomes true, or when the page cannot be drawn.
        bool Render(int index, int width, int height, std::vector<BYTE>& pixels, const std::atomic<bool>* cancel);

    private:
        Document() = default;
        static int GetBlock(void* param, unsigned long position, unsigned char* buffer, unsigned long size);

        ComPtr<IStream> m_stream;
        void* m_document = nullptr; // FPDF_DOCUMENT
        std::vector<PageSize> m_sizes;
        struct FileAccess;
        std::unique_ptr<FileAccess> m_access;
    };

    // Largest number of pages read (page sizes are read when opening).
    inline constexpr int MaxPages = 100000;
}
