using System.Runtime.InteropServices;
using Mavue.Core.Formats;
using Mavue.QuickView.Diagnostics;
using Mavue.QuickView.Host.Content;
using Mavue.Shell;

namespace Mavue.QuickView.Host;

/// <summary>
/// File operations offered from Quick View (buttons, Ctrl+C, Ctrl+O, Enter): open in Mavue, "Open with", copy to the
/// clipboard. Never changes, moves or executes the previewed file. Dragging out of Quick View (copy by default) and
/// sharing belong here as well.
/// </summary>
internal sealed class QuickViewFileActions
{
    private readonly QuickViewWindow _window;
    private readonly QuickViewTimeline _timeline;
    private readonly IPreviewHost _host;
    private readonly Func<IPreviewContent?> _active;
    private readonly Func<FileFormat> _format;
    private readonly Action<string> _close;

    /// <param name="active">The content showing the current item (playback, selection and animation commands).</param>
    /// <param name="format">Format of the current item (copying an image also puts its pixels on the clipboard).</param>
    /// <param name="close">Closes Quick View with the given reason (after handing the file to Mavue).</param>
    public QuickViewFileActions(QuickViewWindow window, QuickViewTimeline timeline, IPreviewHost host, Func<IPreviewContent?> active, Func<FileFormat> format, Action<string> close)
    {
        _window = window;
        _timeline = timeline;
        _host = host;
        _active = active;
        _format = format;
        _close = close;
    }

    /// <summary>
    /// A drag of the file out of Quick View starts (file name, or the image while it fits): the file goes on the data
    /// package read-only, and the drop target may only copy it (<see cref="DragOutPolicy"/>; Quick View never moves the
    /// previewed file). Folders and missing files are not dragged.
    /// </summary>
    public async void PrepareDrag(Microsoft.UI.Xaml.DragStartingEventArgs args, Core.Viewing.FileFacts? facts)
    {
        if (!_host.IsVisible || _host.CurrentPath is not { } path || facts is null || !Core.Viewing.DragOutPolicy.CanDrag(facts))
        {
            args.Cancel = true;
            return;
        }

        Microsoft.UI.Xaml.DragOperationDeferral deferral = args.GetDeferral();
        try
        {
            Windows.Storage.StorageFile file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            args.Data.SetStorageItems([file], readOnly: true);
            args.Data.Properties.Title = file.Name;
            args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            args.AllowedOperations = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            _timeline.Mark(_host.CurrentRequest, "drag-out", QuickViewTimeline.Now, new Dictionary<string, object?> { ["operations"] = args.AllowedOperations.ToString() });
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or FileNotFoundException or IOException or ArgumentException)
        {
            args.Cancel = true;
            _timeline.Mark(_host.CurrentRequest, "file-command-error", QuickViewTimeline.Now, new Dictionary<string, object?> { ["type"] = ex.GetType().Name, ["hresult"] = ex.HResult });
        }
        finally
        {
            deferral.Complete();
        }
    }

    public async void Execute(QuickViewFileCommand command)
    {
        if (!_host.IsVisible || _host.CurrentPath is not { } path)
        {
            return;
        }

        _timeline.Mark(_host.CurrentRequest, "file-command", QuickViewTimeline.Now, new Dictionary<string, object?> { ["command"] = command.ToString() });
        try
        {
            switch (command)
            {
                case QuickViewFileCommand.OpenInMavue:
                    // Started while Quick View is the active window, so Mavue may take the foreground; then Quick View closes.
                    if (ShellActions.LaunchMavue([path]))
                    {
                        _close("open-in-mavue");
                    }

                    break;
                case QuickViewFileCommand.OpenWith:
                    (_active() as IPlaybackContent)?.PauseIfPlaying();
                    await ShellActions.OpenWithAsync(_window.Handle, path);
                    break;
                case QuickViewFileCommand.Copy when _active() is ISelectionContent { HasSelection: true } selection:
                    await selection.CopySelectionAsync();
                    break;
                case QuickViewFileCommand.Copy:
                    await ShellActions.CopyToClipboardAsync(path, _format());
                    break;
                case QuickViewFileCommand.ToggleAnimation:
                    (_active() as IAnimatedContent)?.ToggleAnimation();
                    break;
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or FileNotFoundException or IOException)
        {
            _timeline.Mark(_host.CurrentRequest, "file-command-error", QuickViewTimeline.Now, new Dictionary<string, object?> { ["type"] = ex.GetType().Name, ["hresult"] = ex.HResult });
        }
    }
}
