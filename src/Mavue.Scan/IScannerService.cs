namespace Mavue.Scan;

/// <summary>Scanner driver families Mavue can talk to (SPEC §17).</summary>
public enum ScannerProtocol
{
    Wia,
    Twain,
}

/// <summary>A scanner visible to Mavue.</summary>
public sealed record ScannerInfo(string Id, string DisplayName, ScannerProtocol Protocol);

/// <summary>Enumerates scanners across protocols.</summary>
public interface IScannerService
{
    ValueTask<IReadOnlyList<ScannerInfo>> FindScannersAsync(CancellationToken cancellationToken);
}
