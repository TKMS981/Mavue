using System.Diagnostics;
using System.Runtime.InteropServices;
using Mavue.Core.Formats;
using Microsoft.Win32;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Mavue.Shell;

/// <summary>
/// File commands shared by the main window and Quick View, done the way Windows does them: the system "Open with"
/// dialog, File Explorer with the file selected, the Properties dialog, the clipboard, and starting Mavue itself.
/// Nothing here changes a file.
/// </summary>
public static partial class ShellActions
{
    private const int OaifAllowRegistration = 0x1;
    private const int OaifExec = 0x4;
    private const uint ShopFilePath = 0x2;
    private const string AppPathsKey = @"Software\Microsoft\Windows\CurrentVersion\App Paths\" + AppRegistration.ExecutableName;

    /// <summary>
    /// Shows Windows' "Open with" dialog for <paramref name="path"/> and starts the chosen app. Runs on its own STA
    /// thread (the dialog is modal), so the caller's window keeps drawing; completes when the dialog closes.
    /// </summary>
    public static Task<bool> OpenWithAsync(nint owner, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(ShowOpenWith(owner, path));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Mavue.OpenWith",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    /// <summary>Opens the folder of <paramref name="path"/> in File Explorer with the file selected.</summary>
    public static bool ShowInFolder(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (SHParseDisplayName(path, 0, out nint item, 0, out _) < 0 || item == 0)
        {
            return false;
        }

        try
        {
            return SHOpenFolderAndSelectItems(item, 0, 0, 0) >= 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(item);
        }
    }

    /// <summary>Shows the Windows Properties dialog of <paramref name="path"/> (modeless; call on a UI thread).</summary>
    public static bool ShowProperties(nint owner, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return SHObjectProperties(owner, ShopFilePath, path, null);
    }

    /// <summary>
    /// Puts <paramref name="path"/> on the clipboard as a file (paste in File Explorer or a mail) and, for an image, also
    /// as a picture (paste in a document or a chat). Call on a UI (STA) thread.
    /// </summary>
    public static async Task CopyToClipboardAsync(string path, FileFormat format)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetStorageItems([file], readOnly: true);
        if (FileFormatKinds.IsImage(format) && format is not (FileFormat.Svg or FileFormat.CameraRaw))
        {
            package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        }

        Clipboard.SetContent(package);
        Clipboard.Flush(); // keep it on the clipboard after Mavue closes
    }

    /// <summary>Files on the clipboard (copied in File Explorer), or an empty list.</summary>
    public static async Task<IReadOnlyList<string>> FilesOnClipboardAsync()
    {
        DataPackageView view = Clipboard.GetContent();
        if (!view.Contains(StandardDataFormats.StorageItems))
        {
            return [];
        }

        IReadOnlyList<IStorageItem> items = await view.GetStorageItemsAsync();
        return items.OfType<IStorageFile>().Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToArray();
    }

    /// <summary>
    /// Mavue.exe: next to this program (installed layout), or where it registered itself (App Paths, see
    /// <see cref="AppRegistration"/>). Null when it cannot be found.
    /// </summary>
    public static string? FindMavueExecutable()
    {
        if (Path.GetDirectoryName(Environment.ProcessPath) is { } directory)
        {
            // Release layout (ZIP and MSIX): Mavue.exe and the Quick View host share one folder.
            string candidate = Path.Combine(directory, AppRegistration.ExecutableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (RegistryKey hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using RegistryKey? key = hive.OpenSubKey(AppPathsKey, writable: false);
            if (key?.GetValue(string.Empty) is string registered && Path.IsPathFullyQualified(registered.Trim('"')) && File.Exists(registered.Trim('"')))
            {
                return registered.Trim('"');
            }
        }

        return null;
    }

    /// <summary>Starts Mavue with <paramref name="paths"/>; false when Mavue.exe is not found or cannot start.</summary>
    public static bool LaunchMavue(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (FindMavueExecutable() is not { } executable)
        {
            return false;
        }

        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (string path in paths)
        {
            start.ArgumentList.Add(path);
        }

        try
        {
            using Process? process = Process.Start(start);
            return process is not null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens a web or mail address from a document with the default app. Only http, https and mailto are opened;
    /// anything else (file:, javascript:, other programs' schemes) is refused.
    /// </summary>
    public static bool OpenUri(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) || parsed.Scheme is not ("http" or "https" or "mailto"))
        {
            return false;
        }

        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Opens a Windows Settings page (e.g. <c>ms-settings:defaultapps</c>).</summary>
    public static bool OpenSettingsPage(string uri)
    {
        ArgumentException.ThrowIfNullOrEmpty(uri);
        if (!uri.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only ms-settings: pages are opened.", nameof(uri));
        }

        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static unsafe bool ShowOpenWith(nint owner, string path)
    {
        fixed (char* file = path)
        {
            var info = new OpenAsInfo { File = (nint)file, Class = 0, Flags = OaifAllowRegistration | OaifExec };
            return SHOpenWithDialog(owner, &info) >= 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenAsInfo
    {
        public nint File;
        public nint Class;
        public int Flags;
    }

    [LibraryImport("shell32.dll")]
    private static unsafe partial int SHOpenWithDialog(nint parent, OpenAsInfo* info);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHParseDisplayName(string name, nint bindContext, out nint item, uint attributesIn, out uint attributesOut);

    [LibraryImport("shell32.dll")]
    private static partial int SHOpenFolderAndSelectItems(nint folder, uint count, nint items, uint flags);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SHObjectProperties(nint owner, uint type, string name, string? page);
}
