using System.Runtime.InteropServices;

namespace Mavue.Shell.Tests;

/// <summary>
/// Loads the native command DLL directly (no registration, no Explorer) and calls it through COM vtables.
/// Skipped when the DLL has not been built (it needs the MSVC Build Tools: tools/build-native.ps1).
/// Invoke is only called with no items: with real items it would ask File Explorer to start the host.
/// </summary>
[Trait("Category", "Shell")]
public sealed unsafe class NativeCommandTests
{
    private static readonly Guid IidClassFactory = new("00000001-0000-0000-C000-000000000046");
    private static readonly Guid IidExplorerCommand = new("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9");
    private const int EInvalidArg = unchecked((int)0x80070057);
    private const int ClassNotAvailable = unchecked((int)0x80040111);

    private static string DllPath => Path.Combine(IdentityPackageManifestTests.RepoRoot(), "artifacts", "native", "win-x64", "Mavue.Shell.Native.dll");

    private static nint Load()
    {
        Assert.SkipUnless(File.Exists(DllPath), "Native command DLL not built (tools/build-native.ps1 needs the MSVC Build Tools).");
        return NativeLibrary.Load(DllPath);
    }

    private static nint CreateCommand(nint library)
    {
        var getClassObject = (delegate* unmanaged[Stdcall]<Guid*, Guid*, nint*, int>)NativeLibrary.GetExport(library, "DllGetClassObject");
        Guid clsid = new(IdentityPackageManifest.CommandClsid);
        Guid iidFactory = IidClassFactory;
        nint factory;
        Assert.Equal(0, getClassObject(&clsid, &iidFactory, &factory));

        nint* factoryTable = *(nint**)factory;
        Guid iidCommand = IidExplorerCommand;
        nint command;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)factoryTable[3])(factory, 0, &iidCommand, &command);
        ((delegate* unmanaged[Stdcall]<nint, uint>)factoryTable[2])(factory);
        Assert.Equal(0, hr);
        return command;
    }

    private static nint* Table(nint unknown) => *(nint**)unknown;

    private static string? CallString(nint command, int slot)
    {
        char* text = null;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint, char**, int>)Table(command)[slot])(command, 0, &text);
        Assert.Equal(0, hr);
        string? value = new(text);
        Marshal.FreeCoTaskMem((nint)text);
        return value;
    }

    [Fact]
    public void Command_ReportsTitleIconStateAndFlags()
    {
        nint library = Load();
        nint command = CreateCommand(library);
        try
        {
            Assert.Equal("Mavue Quick View", CallString(command, 3)); // GetTitle
            Assert.EndsWith(@"\Mavue.QuickView.Host.exe,0", CallString(command, 4), StringComparison.OrdinalIgnoreCase); // GetIcon

            Guid canonical;
            Assert.Equal(0, ((delegate* unmanaged[Stdcall]<nint, Guid*, int>)Table(command)[6])(command, &canonical));
            Assert.Equal(new Guid(IdentityPackageManifest.CommandClsid), canonical);

            uint state = 99;
            Assert.Equal(0, ((delegate* unmanaged[Stdcall]<nint, nint, int, uint*, int>)Table(command)[7])(command, 0, 0, &state));
            Assert.Equal(0u, state); // ECS_ENABLED

            uint flags = 99;
            Assert.Equal(0, ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Table(command)[9])(command, &flags));
            Assert.Equal(0u, flags); // ECF_DEFAULT
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<nint, uint>)Table(command)[2])(command);
        }
    }

    [Fact]
    public void Invoke_WithoutItems_StartsNothing()
    {
        nint library = Load();
        nint command = CreateCommand(library);
        try
        {
            int hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Table(command)[8])(command, 0, 0);
            Assert.Equal(EInvalidArg, hr);
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<nint, uint>)Table(command)[2])(command);
        }
    }

    [Fact]
    public void UnknownClass_IsRejected_AndModuleCanUnload()
    {
        nint library = Load();
        var getClassObject = (delegate* unmanaged[Stdcall]<Guid*, Guid*, nint*, int>)NativeLibrary.GetExport(library, "DllGetClassObject");
        Guid other = Guid.NewGuid();
        Guid iidFactory = IidClassFactory;
        nint factory = 1;
        Assert.Equal(ClassNotAvailable, getClassObject(&other, &iidFactory, &factory));
        Assert.Equal(0, factory);

        var canUnload = (delegate* unmanaged[Stdcall]<int>)NativeLibrary.GetExport(library, "DllCanUnloadNow");
        Assert.Equal(0, canUnload()); // S_OK: no objects alive
    }
}
