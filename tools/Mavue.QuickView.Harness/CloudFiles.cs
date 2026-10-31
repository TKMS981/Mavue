using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Mavue.QuickView.Harness;

/// <summary>
/// A throw-away cloud sync root (Windows Cloud Files API) for testing that Quick View never downloads cloud-only
/// files: placeholders are created dehydrated, and every request for their data (FETCH_DATA) is recorded with the
/// requesting process and refused at once. Registered under %TEMP% only for the test and unregistered afterwards; the
/// user's own sync roots (OneDrive) are never touched.
/// </summary>
internal sealed unsafe partial class CloudFiles : IDisposable
{
    private const uint CfCallbackTypeFetchData = 0;
    private const uint CfCallbackTypeNone = 0xFFFFFFFF;
    private const uint CfConnectFlagRequireProcessInfo = 0x2;
    private const uint CfConnectFlagRequireFullFilePath = 0x4;
    private const uint CfPlaceholderCreateFlagMarkInSync = 0x2;
    private const uint StatusCloudFileUnsuccessful = 0xC000CF0B;

    private static readonly ConcurrentQueue<(int ProcessId, string Path)> s_fetches = new();

    private readonly string _root;
    private long _connection;
    private bool _registered;

    private CloudFiles(string root)
    {
        _root = root;
    }

    /// <summary>Requests for placeholder data seen so far (process id, path).</summary>
    public static IReadOnlyList<(int ProcessId, string Path)> Fetches => [.. s_fetches];

    /// <summary>Registers <paramref name="root"/> as a sync root and connects the recording provider.</summary>
    public static CloudFiles Create(string root)
    {
        Directory.CreateDirectory(root);
        var files = new CloudFiles(root);
        byte[] identity = "mavue-e2e"u8.ToArray();
        fixed (char* name = "Mavue E2E cloud test", version = "1.0")
        fixed (byte* id = identity)
        {
            var registration = new SyncRegistration
            {
                StructSize = (uint)sizeof(SyncRegistration),
                ProviderName = (nint)name,
                ProviderVersion = (nint)version,
                SyncRootIdentity = (nint)id,
                SyncRootIdentityLength = (uint)identity.Length,
                ProviderId = new Guid("6E2B5C10-3F0A-4F59-9A51-4D6B2E7A1C42"),
            };
            var policies = new SyncPolicies
            {
                StructSize = (uint)sizeof(SyncPolicies),
                HydrationPrimary = 2,  // CF_HYDRATION_POLICY_FULL
                PopulationPrimary = 3, // CF_POPULATION_POLICY_ALWAYS_FULL: no placeholder requests
            };
            Check(CfRegisterSyncRoot(root, &registration, &policies, 0), "CfRegisterSyncRoot");
            files._registered = true;
        }

        CallbackRegistration* table = stackalloc CallbackRegistration[2];
        table[0] = new CallbackRegistration { Type = CfCallbackTypeFetchData, Callback = (nint)(delegate* unmanaged[Stdcall]<byte*, byte*, void>)&OnFetchData };
        table[1] = new CallbackRegistration { Type = CfCallbackTypeNone, Callback = 0 };
        long key;
        Check(CfConnectSyncRoot(root, table, 0, CfConnectFlagRequireProcessInfo | CfConnectFlagRequireFullFilePath, &key), "CfConnectSyncRoot");
        files._connection = key;
        return files;
    }

    /// <summary>Creates dehydrated placeholders (cloud-only files) of the given sizes in the root.</summary>
    public void CreatePlaceholders(IEnumerable<(string Name, long Size)> files)
    {
        long now = DateTime.UtcNow.ToFileTimeUtc();
        foreach ((string name, long size) in files)
        {
            byte[] identity = System.Text.Encoding.UTF8.GetBytes(name);
            fixed (char* relative = name)
            fixed (byte* id = identity)
            {
                var info = new PlaceholderCreateInfo
                {
                    RelativeFileName = (nint)relative,
                    CreationTime = now,
                    LastAccessTime = now,
                    LastWriteTime = now,
                    ChangeTime = now,
                    FileAttributes = 0x80, // FILE_ATTRIBUTE_NORMAL
                    FileSize = size,
                    FileIdentity = (nint)id,
                    FileIdentityLength = (uint)identity.Length,
                    Flags = CfPlaceholderCreateFlagMarkInSync,
                };
                uint processed;
                Check(CfCreatePlaceholders(_root, &info, 1, 0, &processed), "CfCreatePlaceholders");
                Check(info.Result, "placeholder " + name);
            }
        }
    }

    /// <summary>True while the file is still cloud-only (its data was never downloaded).</summary>
    public static bool IsDehydrated(string path) =>
        ((uint)File.GetAttributes(path) & (0x00400000 /* RECALL_ON_DATA_ACCESS */ | 0x1000 /* OFFLINE */)) != 0;

    public void Dispose()
    {
        if (_connection != 0)
        {
            _ = CfDisconnectSyncRoot(_connection);
            _connection = 0;
        }

        if (_registered)
        {
            _ = CfUnregisterSyncRoot(_root);
            _registered = false;
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in %TEMP%.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnFetchData(byte* info, byte* parameters)
    {
        // CF_CALLBACK_INFO (64-bit): ConnectionKey @8, NormalizedPath @104, TransferKey @112, ProcessInfo @136, RequestKey @144.
        int processId = -1;
        string path = string.Empty;
        try
        {
            nint processInfo = *(nint*)(info + 136);
            if (processInfo != 0)
            {
                processId = *(int*)(processInfo + 4); // CF_PROCESS_INFO.ProcessId
            }

            nint normalized = *(nint*)(info + 104);
            path = normalized != 0 ? Marshal.PtrToStringUni(normalized) ?? string.Empty : string.Empty;
        }
        catch (AccessViolationException)
        {
            // Recorded without details.
        }

        s_fetches.Enqueue((processId, path));

        // Refuse at once so the reader is not blocked until the provider timeout.
        var operation = new OperationInfo
        {
            StructSize = (uint)sizeof(OperationInfo),
            Type = 0, // CF_OPERATION_TYPE_TRANSFER_DATA
            ConnectionKey = *(long*)(info + 8),
            TransferKey = *(long*)(info + 112),
            RequestKey = *(uint*)info >= 152 ? *(long*)(info + 144) : 0,
        };
        var transfer = new TransferDataParameters
        {
            ParamSize = (uint)sizeof(TransferDataParameters),
            CompletionStatus = unchecked((int)StatusCloudFileUnsuccessful),
            Offset = *(long*)(parameters + 16), // FetchData.RequiredFileOffset
            Length = *(long*)(parameters + 24), // FetchData.RequiredLength
        };
        _ = CfExecute(&operation, &transfer);
    }

    private static void Check(int hresult, string what)
    {
        if (hresult < 0)
        {
            throw new InvalidOperationException($"{what} failed: 0x{hresult:X8}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SyncRegistration
    {
        public uint StructSize;
        public nint ProviderName;
        public nint ProviderVersion;
        public nint SyncRootIdentity;
        public uint SyncRootIdentityLength;
        public nint FileIdentity;
        public uint FileIdentityLength;
        public Guid ProviderId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SyncPolicies
    {
        public uint StructSize;
        public ushort HydrationPrimary;
        public ushort HydrationModifier;
        public ushort PopulationPrimary;
        public ushort PopulationModifier;
        public uint InSync;
        public uint HardLink;
        public uint PlaceholderManagement;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CallbackRegistration
    {
        public uint Type;
        public nint Callback;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PlaceholderCreateInfo
    {
        public nint RelativeFileName;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
        public long FileSize;
        public nint FileIdentity;
        public uint FileIdentityLength;
        public uint Flags;
        public int Result;
        public long CreateUsn;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OperationInfo
    {
        public uint StructSize;
        public uint Type;
        public long ConnectionKey;
        public long TransferKey;
        public nint CorrelationVector;
        public nint SyncStatus;
        public long RequestKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TransferDataParameters
    {
        public uint ParamSize;
        public uint UnionAlignment; // the union of CF_OPERATION_PARAMETERS starts at offset 8
        public uint Flags;
        public int CompletionStatus;
        public nint Buffer;
        public long Offset;
        public long Length;
    }

    [LibraryImport("cldapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CfRegisterSyncRoot(string syncRootPath, SyncRegistration* registration, SyncPolicies* policies, uint flags);

    [LibraryImport("cldapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CfUnregisterSyncRoot(string syncRootPath);

    [LibraryImport("cldapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CfConnectSyncRoot(string syncRootPath, CallbackRegistration* callbacks, nint context, uint flags, long* connectionKey);

    [LibraryImport("cldapi.dll")]
    private static partial int CfDisconnectSyncRoot(long connectionKey);

    [LibraryImport("cldapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CfCreatePlaceholders(string baseDirectoryPath, PlaceholderCreateInfo* placeholders, uint count, uint flags, uint* processed);

    [LibraryImport("cldapi.dll")]
    private static partial int CfExecute(OperationInfo* operation, TransferDataParameters* parameters);
}
