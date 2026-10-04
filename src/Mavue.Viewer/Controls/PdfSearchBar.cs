using Mavue.Pdf;
using Mavue.Viewer.Playback;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace Mavue.Viewer.Controls;

/// <summary>
/// Texts of the shared PDF controls. The library has no resources of its own (an unpackaged app does not merge them),
/// so each host passes a lookup for these keys from its resources.
/// </summary>
public static class PdfUiText
{
    public const string SearchPlaceholder = "Pdf_SearchPlaceholder";
    public const string SearchNext = "Pdf_SearchNext";
    public const string SearchPrevious = "Pdf_SearchPrevious";
    public const string MatchCase = "Pdf_MatchCase";
    public const string Searching = "Pdf_Searching";

    /// <summary>"{0} / {1} pages, {2} found".</summary>
    public const string SearchProgress = "Pdf_SearchProgress";
    public const string NoMatches = "Pdf_NoMatches";

    /// <summary>"{0} / {1}".</summary>
    public const string MatchPosition = "Pdf_MatchPosition";

    /// <summary>"p. {0}".</summary>
    public const string ResultPage = "Pdf_ResultPage";

    /// <summary>"Page {0}".</summary>
    public const string PageItem = "Pdf_PageItem";
    public const string TabPages = "Pdf_TabPages";
    public const string TabOutline = "Pdf_TabOutline";
    public const string TabResults = "Pdf_TabResults";
    public const string NoOutline = "Pdf_NoOutline";
    public const string Close = "Pdf_Close";
    public const string PresentationHint = "Pdf_PresentationHint";

    /// <summary>Every key, for hosts' resource tests.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        SearchPlaceholder, SearchNext, SearchPrevious, MatchCase, Searching, SearchProgress, NoMatches, MatchPosition,
        ResultPage, PageItem, TabPages, TabOutline, TabResults, NoOutline, Close, PresentationHint,
    ];
}

/// <summary>
/// Find in a PDF (shared by the main window and Quick View): Enter / F3 next, Shift+Enter / Shift+F3 previous,
/// Esc closes; the results are highlighted by the session's view and listed by <see cref="PdfSidebar"/>.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "A control lives as long as its window; the token source only cancels a search in flight.")]
public sealed partial class PdfSearchBar : UserControl
{
    private readonly Func<string, string> _text;
    private readonly TextBox _box;
    private readonly CheckBox _matchCase;
    private readonly TextBlock _status;
    private PdfSession? _session;
    private CancellationTokenSource? _search;
    private string? _searchedQuery;
    private bool _searchedMatchCase;
    private string? _searchedPath;

    /// <param name="text">Texts for <see cref="PdfUiText"/> keys.</param>
    public PdfSearchBar(Func<string, string> text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _text = text;
        _box = new TextBox { Width = 280, PlaceholderText = text(PdfUiText.SearchPlaceholder) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_box, "SearchBox");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_box, text(PdfUiText.SearchPlaceholder));
        _box.KeyDown += OnBoxKeyDown;
        Button previous = IconButton("", text(PdfUiText.SearchPrevious) + " (Shift+F3)", () => _ = FindAsync(-1));
        Button next = IconButton("", text(PdfUiText.SearchNext) + " (F3)", () => _ = FindAsync(+1));
        _matchCase = new CheckBox { Content = text(PdfUiText.MatchCase), MinWidth = 0 };
        _matchCase.Click += (_, _) => _ = FindAsync(0);
        _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_status, "SearchStatus");
        Button close = IconButton("", text(PdfUiText.Close) + " (Esc)", Close);

        var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(12, 0, 12, 8) };
        UIElement[] parts = [_box, previous, next, _matchCase, _status, close];
        for (int i = 0; i < parts.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 4 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetColumn((FrameworkElement)parts[i], i);
            grid.Children.Add(parts[i]);
        }

        Content = grid;
        Visibility = Visibility.Collapsed;
    }

    /// <summary>The bar was closed (the host gives the keyboard back to the document).</summary>
    public event Action? Closed;

    /// <summary>Diagnostics (tests): "search" and "search-step".</summary>
    public PlaybackTrace? Trace { get; set; }

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>True while the search box has the keyboard (hosts leave Space, arrows and Esc to it).</summary>
    public bool HasFocus => _box.FocusState != FocusState.Unfocused;

    /// <summary>There are results to step through with F3.</summary>
    public bool HasResults => _session?.Matches.Count > 0;

    /// <summary>The session searched (set by the host once).</summary>
    public PdfSession? Session
    {
        get => _session;
        set
        {
            if (_session is not null)
            {
                _session.Closed -= OnSessionClosed;
                _session.Opened -= OnSessionClosed;
            }

            _session = value;
            if (value is not null)
            {
                value.Closed += OnSessionClosed;
                value.Opened += OnSessionClosed;
            }
        }
    }

    /// <summary>Shows the bar and puts the keyboard in the search box.</summary>
    public void Open()
    {
        if (_session?.IsOpen != true)
        {
            return;
        }

        Visibility = Visibility.Visible;
        _box.Focus(FocusState.Keyboard);
        _box.SelectAll();
    }

    /// <summary>Hides the bar and removes the highlights.</summary>
    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        _search?.Cancel();
        Visibility = Visibility.Collapsed;
        _searchedQuery = null;
        _status.Text = string.Empty;
        _session?.ClearSearch();
        Closed?.Invoke();
    }

    /// <summary>
    /// Searches when the query (or "match case") changed; otherwise moves to the next (+1) or previous (-1) result,
    /// wrapping around. 0 searches again.
    /// </summary>
    public async Task FindAsync(int direction)
    {
        if (_session is not { IsOpen: true } session)
        {
            return;
        }

        string query = _box.Text.Trim();
        bool matchCase = _matchCase.IsChecked == true;
        if (query.Length == 0)
        {
            session.ClearSearch();
            _status.Text = string.Empty;
            return;
        }

        if (direction != 0 && query == _searchedQuery && matchCase == _searchedMatchCase && string.Equals(session.Path, _searchedPath, StringComparison.OrdinalIgnoreCase))
        {
            if (await session.StepMatchAsync(direction))
            {
                UpdateStatus();
                Trace?.Invoke("search-step", new Dictionary<string, object?> { ["current"] = session.CurrentMatch, ["page"] = session.Matches[session.CurrentMatch].PageIndex });
            }

            return;
        }

        _search?.Cancel();
        _search = new CancellationTokenSource();
        CancellationToken cancellation = _search.Token;
        _searchedQuery = query;
        _searchedMatchCase = matchCase;
        _searchedPath = session.Path;
        _status.Text = _text(PdfUiText.Searching);
        bool finished = false;
        var progress = new Progress<(int Pages, int Matches)>(p =>
        {
            if (!cancellation.IsCancellationRequested && !finished) // reports arrive asynchronously, possibly after the end
            {
                _status.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture, _text(PdfUiText.SearchProgress), p.Pages, session.PageCount, p.Matches);
            }
        });
        IReadOnlyList<PdfTextMatch> matches;
        try
        {
            matches = await session.SearchAsync(query, matchCase, progress, cancellation);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        finished = true;
        if (cancellation.IsCancellationRequested)
        {
            return;
        }

        UpdateStatus();
        Trace?.Invoke("search", new Dictionary<string, object?> { ["query"] = query, ["matches"] = matches.Count, ["current"] = session.CurrentMatch });
    }

    private void UpdateStatus()
    {
        int count = _session?.Matches.Count ?? 0;
        _status.Text = count == 0
            ? _text(PdfUiText.NoMatches)
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, _text(PdfUiText.MatchPosition), (_session?.CurrentMatch ?? 0) + 1, count);
    }

    private void OnSessionClosed()
    {
        // Another document: the old query is not applied to it.
        _search?.Cancel();
        _searchedQuery = null;
        if (IsOpen)
        {
            Visibility = Visibility.Collapsed;
            _status.Text = string.Empty;
            Closed?.Invoke();
        }
    }

    private void OnBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Enter or VirtualKey.F3:
                e.Handled = true;
                _ = FindAsync(shift ? -1 : +1);
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                Close();
                break;
            case VirtualKey.Space:
                e.Handled = true; // typed into the box; never reaches the host (Quick View closes on Space)
                break;
        }
    }

    private static Button IconButton(string glyph, string name, Action click)
    {
        var button = new Button { Content = glyph, FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        button.Click += (_, _) => click();
        return button;
    }
}
