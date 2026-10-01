namespace Mavue.Pdf;

/// <summary>
/// Opens PDF documents. Implementations are swappable (docs/ARCHITECTURE.md §1, principle 3).
/// </summary>
public interface IPdfEngine
{
    /// <summary>Opens a document from a seekable stream without loading it fully into memory.</summary>
    /// <param name="stream">Seekable, readable stream. Ownership stays with the caller.</param>
    /// <param name="password">Password for encrypted documents, if known.</param>
    /// <param name="cancellationToken">Cancels opening.</param>
    ValueTask<IPdfDocument> OpenAsync(Stream stream, string? password, CancellationToken cancellationToken);
}

/// <summary>An open PDF document. Page objects are loaded lazily.</summary>
public interface IPdfDocument : IAsyncDisposable
{
    int PageCount { get; }

    /// <summary>Page size in PDF points (1/72 inch), with page rotation applied.</summary>
    (double Width, double Height) GetPageSize(int pageIndex);
}
