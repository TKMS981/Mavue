using System.Runtime.InteropServices;

namespace Mavue.Viewer.Workers;

/// <summary>
/// Lowers a worker process's priority: below-normal CPU priority and low memory priority (its pages are trimmed
/// first). Windows' background mode (<c>PROCESS_MODE_BACKGROUND_BEGIN</c>) is not used: in that mode the HEIF/HEVC
/// decoder fails with E_OUTOFMEMORY (measured 2026-11-02), so HEIC photos could not be converted or compared.
/// The job object of <see cref="WorkerSession"/> still caps memory and CPU.
/// </summary>
public static partial class WorkerPriority
{
    private const uint BelowNormalPriorityClass = 0x00004000;
    private const int ProcessMemoryPriority = 0;
    private const uint MemoryPriorityLow = 2;

    public static void Lower()
    {
        nint self = GetCurrentProcess();
        SetPriorityClass(self, BelowNormalPriorityClass);
        uint memoryPriority = MemoryPriorityLow;
        unsafe
        {
            SetProcessInformation(self, ProcessMemoryPriority, &memoryPriority, sizeof(uint));
        }
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetPriorityClass(nint process, uint priorityClass);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetProcessInformation(nint process, int informationClass, void* information, uint size);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();
}
