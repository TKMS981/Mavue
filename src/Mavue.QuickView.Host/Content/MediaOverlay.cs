using System.Runtime.InteropServices.WindowsRuntime;
using Mavue.Core.Viewing;
using Mavue.Viewer.Playback;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Mavue.QuickView.Host.Content;

/// <summary>
/// Quick View's light media controls (docs/ARCHITECTURE.md §4.7.11), in place of the built-in transport controls:
/// one bar along the bottom with play/pause, the position, a seek slider, the duration and mute; for audio also the
/// waveform above it (click or drag to seek). Over a playing video the bar fades out after a few seconds without
/// pointer movement and comes back on movement or wheel seeking, so the picture stays clear. The controls never keep
/// the keyboard (Enter, Ctrl+arrows, Space and Esc keep their meaning). Created on the first media item.
/// </summary>
internal sealed class MediaOverlay
{
    private const double SliderRange = 1000;
    private const double WaveformHeight = 88;

    private readonly QuickViewWindow _window;
    private readonly ResourceLoader _strings;
    private readonly Grid _root;
    private readonly Border _bar;
    private readonly Button _play;
    private readonly FontIcon _playIcon;
    private readonly Button _mute;
    private readonly FontIcon _muteIcon;
    private readonly Slider _slider;
    private readonly TextBlock _position;
    private readonly TextBlock _duration;
    private readonly Grid _waveformHost;
    private readonly Microsoft.UI.Xaml.Controls.Image _waveformBase;
    private readonly Microsoft.UI.Xaml.Controls.Image _waveformPlayed;
    private readonly RectangleGeometry _playedClip = new();
    private readonly DispatcherQueueTimer _hideTimer;
    private bool _video;
    private bool _playing;
    private bool _updating;
    private bool _dragging;
    private bool _waveformDragging;
    private TimeSpan _total;
    private AudioWaveform? _waveform;
    private double _fraction;
    private (bool Playing, bool Muted)? _shownState;

    public MediaOverlay(QuickViewWindow window, ResourceLoader strings, DispatcherQueue dispatcher)
    {
        _window = window;
        _strings = strings;

        _playIcon = new FontIcon { Glyph = "\uE768", FontSize = 16 };
        _play = ToolButton(_playIcon, "QuickViewMediaPlay");
        _play.Click += (_, _) => PlayPauseClicked?.Invoke();
        _muteIcon = new FontIcon { Glyph = "\uE767", FontSize = 16 };
        _mute = ToolButton(_muteIcon, "QuickViewMediaMute");
        _mute.Click += (_, _) => MuteClicked?.Invoke();
        _position = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 44, TextAlignment = TextAlignment.Right };
        _duration = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 44 };
        _slider = new Slider
        {
            Minimum = 0,
            Maximum = SliderRange,
            StepFrequency = 1,
            IsThumbToolTipEnabled = false,
            IsTabStop = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0),
        };
        AutomationProperties.SetName(_slider, strings.GetString("QuickView_Media_Position"));
        AutomationProperties.SetAutomationId(_slider, "QuickViewMediaPosition");
        ToolTipService.SetToolTip(_slider, strings.GetString("QuickView_Media_Position"));
        _slider.ValueChanged += OnSliderChanged;
        _slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => _dragging = true), handledEventsToo: true);
        _slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => _dragging = false), handledEventsToo: true);
        _slider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => _dragging = false), handledEventsToo: true);

        var row = new Grid { ColumnSpacing = 6 };
        foreach (GridLength width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        }

        Add(row, _play, 0);
        Add(row, _position, 1);
        Add(row, _slider, 2);
        Add(row, _duration, 3);
        Add(row, _mute, 4);
        _bar = new Border
        {
            Child = row,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(16, 0, 16, 16),
            MaxWidth = 760,
            VerticalAlignment = VerticalAlignment.Bottom,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(220) },
        };
        AutomationProperties.SetAutomationId(_bar, "QuickViewMediaBar");

        // Waveform (audio): the drawn peaks, and the same drawing in the "played" color clipped at the position.
        _waveformBase = new Microsoft.UI.Xaml.Controls.Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
        _waveformPlayed = new Microsoft.UI.Xaml.Controls.Image { Stretch = Stretch.Fill, IsHitTestVisible = false, Clip = _playedClip };
        _waveformHost = new Grid
        {
            Height = WaveformHeight,
            MaxWidth = 760,
            Margin = new Thickness(28, 0, 28, 8),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), // takes clicks across the whole band
            Visibility = Visibility.Collapsed,
        };
        _waveformHost.Children.Add(_waveformBase);
        _waveformHost.Children.Add(_waveformPlayed);
        AutomationProperties.SetName(_waveformHost, strings.GetString("QuickView_Media_Waveform"));
        AutomationProperties.SetAutomationId(_waveformHost, "QuickViewWaveform");
        ToolTipService.SetToolTip(_waveformHost, strings.GetString("QuickView_Media_Waveform"));
        _waveformHost.SizeChanged += (_, _) => DrawWaveform();
        _waveformHost.PointerPressed += OnWaveformPressed;
        _waveformHost.PointerMoved += OnWaveformMoved;
        _waveformHost.PointerReleased += (_, e) => EndWaveformDrag(e);
        _waveformHost.PointerCaptureLost += (_, _) => _waveformDragging = false;

        var bottom = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        bottom.Children.Add(_waveformHost);
        bottom.Children.Add(_bar);

        // The layer itself takes the pointer (transparent background) only to bring the faded bar back on movement.
        _root = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        _root.Children.Add(bottom);
        _root.PointerMoved += (_, _) => Poke();
        AutomationProperties.SetAutomationId(_root, "QuickViewMediaOverlay");

        _hideTimer = dispatcher.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromSeconds(2.5);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) =>
        {
            if (_video && _playing && !_dragging)
            {
                _bar.Opacity = 0;
            }
        };
    }

    public FrameworkElement Root => _root;

    public event Action? PlayPauseClicked;

    public event Action? MuteClicked;

    /// <summary>A position chosen on the slider or the waveform (0–1 of the duration), with its source.</summary>
    public event Action<double, string>? SeekRequested;

    /// <summary>Prepares the controls for a new file (video: the bar fades while playing; audio: the waveform band).</summary>
    public void Attach(bool video)
    {
        _video = video;
        _playing = false;
        _total = TimeSpan.Zero;
        _waveform = null;
        _fraction = 0;
        _bar.Background = _window.ThemeBrush("QvOverlayBrush");
        _bar.BorderBrush = _window.ThemeBrush("QvOverlayBorderBrush");
        _duration.Foreground = _window.ThemeBrush("QvSecondaryTextBrush");
        _waveformHost.Visibility = Visibility.Collapsed;
        _waveformBase.Source = null;
        _waveformPlayed.Source = null;
        _shownState = null;
        Update(TimeSpan.Zero, TimeSpan.Zero, playing: false, muted: false);
        _bar.Opacity = 1;
        _hideTimer.Stop();
    }

    /// <summary>Stops the fade timer (the file is gone).</summary>
    public void Detach()
    {
        _hideTimer.Stop();
        _waveform = null;
        _waveformBase.Source = null;
        _waveformPlayed.Source = null;
    }

    /// <summary>Shows the bar again (pointer movement, wheel seeking) and restarts the fade timer over a playing video.</summary>
    public void Poke()
    {
        _bar.Opacity = 1;
        _hideTimer.Stop();
        if (_video && _playing)
        {
            _hideTimer.Start();
        }
    }

    public void Update(TimeSpan position, TimeSpan duration, bool playing, bool muted)
    {
        if (playing != _playing)
        {
            _playing = playing;
            Poke();
        }

        _total = duration;
        if (_shownState != (playing, muted))
        {
            // Names and tooltips change only with the state (the position updates five times a second).
            _shownState = (playing, muted);
            _playIcon.Glyph = playing ? "\uE769" : "\uE768";
            SetText(_play, playing ? "QuickView_Media_Pause" : "QuickView_Media_Play");
            _muteIcon.Glyph = muted ? "\uE74F" : "\uE767";
            SetText(_mute, muted ? "QuickView_Media_Unmute" : "QuickView_Media_Mute");
        }

        _position.Text = MediaControlMath.FormatDuration(position);
        _duration.Text = duration > TimeSpan.Zero ? MediaControlMath.FormatDuration(duration) : "–:––";
        _slider.IsEnabled = duration > TimeSpan.Zero;
        _fraction = duration > TimeSpan.Zero ? Math.Clamp(position / duration, 0, 1) : 0;
        if (!_dragging)
        {
            _updating = true;
            _slider.Value = _fraction * SliderRange;
            _updating = false;
        }

        UpdatePlayedClip();
    }

    /// <summary>A new snapshot of the audio waveform (coarse first, then refined).</summary>
    public void SetWaveform(AudioWaveform waveform)
    {
        _waveform = waveform;
        _waveformHost.Visibility = Visibility.Visible;
        DrawWaveform();
    }

    private void OnSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_updating && _total > TimeSpan.Zero)
        {
            SeekRequested?.Invoke(e.NewValue / SliderRange, "slider");
        }
    }

    private void OnWaveformPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_waveformHost.CapturePointer(e.Pointer))
        {
            _waveformDragging = true;
        }

        SeekToPointer(e, "waveform");
        e.Handled = true;
    }

    private void OnWaveformMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_waveformDragging)
        {
            SeekToPointer(e, "waveform-drag");
        }
    }

    private void EndWaveformDrag(PointerRoutedEventArgs e)
    {
        _waveformDragging = false;
        _waveformHost.ReleasePointerCapture(e.Pointer);
    }

    private void SeekToPointer(PointerRoutedEventArgs e, string source)
    {
        double width = _waveformHost.ActualWidth;
        if (width > 0 && _total > TimeSpan.Zero)
        {
            SeekRequested?.Invoke(Math.Clamp(e.GetCurrentPoint(_waveformHost).Position.X / width, 0, 1), source);
        }
    }

    private void UpdatePlayedClip()
    {
        double width = _waveformHost.ActualWidth;
        _playedClip.Rect = new Windows.Foundation.Rect(0, 0, Math.Max(0, width * _fraction), WaveformHeight);
    }

    /// <summary>Bars of 2 pixels with 1 pixel gaps, centered, at the band's physical size (redrawn on resize).</summary>
    private void DrawWaveform()
    {
        if (_waveform is not { } waveform || _waveformHost.ActualWidth <= 0)
        {
            return;
        }

        double scale = _waveformHost.XamlRoot?.RasterizationScale ?? 1;
        int width = Math.Max(1, (int)(_waveformHost.ActualWidth * scale));
        int height = Math.Max(1, (int)(WaveformHeight * scale));
        int barWidth = Math.Max(2, (int)Math.Round(2 * scale));
        int pitch = barWidth + Math.Max(1, (int)Math.Round(scale));
        float[] columns = MediaControlMath.WaveformColumns(waveform.Peaks, Math.Max(1, width / pitch));
        _waveformBase.Source = Draw(columns, width, height, barWidth, pitch, Color("QvSecondaryTextBrush", 0.55));
        _waveformPlayed.Source = Draw(columns, width, height, barWidth, pitch, Color("QvPrimaryTextBrush", 1));
        UpdatePlayedClip();
    }

    private static WriteableBitmap Draw(float[] columns, int width, int height, int barWidth, int pitch, Windows.UI.Color color)
    {
        var pixels = new byte[width * height * 4];

        // Premultiplied BGRA.
        byte a = color.A;
        byte b = (byte)(color.B * a / 255), g = (byte)(color.G * a / 255), r = (byte)(color.R * a / 255);
        for (int c = 0; c < columns.Length; c++)
        {
            int barHeight = Math.Max(2, (int)(columns[c] * height));
            int top = (height - barHeight) / 2;
            int left = c * pitch;
            for (int y = top; y < top + barHeight; y++)
            {
                int index = ((y * width) + left) * 4;
                for (int x = 0; x < barWidth && left + x < width; x++, index += 4)
                {
                    pixels[index] = b;
                    pixels[index + 1] = g;
                    pixels[index + 2] = r;
                    pixels[index + 3] = a;
                }
            }
        }

        var bitmap = new WriteableBitmap(width, height);
        using (Stream stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(pixels, 0, pixels.Length);
        }

        bitmap.Invalidate();
        return bitmap;
    }

    private Windows.UI.Color Color(string key, double opacity)
    {
        Windows.UI.Color color = _window.ThemeBrush(key) is SolidColorBrush solid ? solid.Color : Microsoft.UI.Colors.Gray;
        color.A = (byte)(color.A * opacity);
        return color;
    }

    private static Button ToolButton(FontIcon icon, string automationId)
    {
        var button = new Button
        {
            Content = icon,
            Width = 36,
            Height = 32,
            Padding = new Thickness(0),
            IsTabStop = false, // the keyboard stays with Quick View (Enter, Ctrl+arrows, Space, Esc)
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }

    private void SetText(Button button, string resource)
    {
        string text = _strings.GetString(resource);
        AutomationProperties.SetName(button, text);
        ToolTipService.SetToolTip(button, text);
    }

    private static void Add(Grid grid, FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }
}
