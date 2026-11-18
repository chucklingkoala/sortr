using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sortr.App.Services;
using Sortr.Core;

namespace Sortr.App.Views;

/// <summary>
/// A reusable, non-modal window that previews one file at a time: images, video/audio, text, or Explorer's
/// thumbnail for anything else. Only the media player keeps a file open, and <see cref="Clear"/> releases it.
/// </summary>
public partial class PreviewWindow : Window
{
    private const int ThumbnailSize = 256;
    // Decoding caps the longer side at this many pixels: sharp on large high-DPI screens, cheap on memory.
    private const int MaxImageSide = 2560;

    private const string PlayGlyph = "";
    private const string PauseGlyph = "";
    private const string VolumeGlyph = "";
    private const string MuteGlyph = "";

    private readonly DispatcherTimer _positionTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    // Bumped by every ShowFile/Clear, so a slow load for an earlier file never overwrites a newer one.
    private int _request;
    private string? _path;
    private string _baseInfo = "";
    private bool _isPlaying;
    private bool _settingSlider;
    private bool _allowClose;

    public PreviewWindow()
    {
        InitializeComponent();
        _positionTimer.Tick += (_, _) => UpdatePosition();
    }

    /// <summary>Raised with -1 or +1 when the user presses ↑/↓ in this window, to step through the file list.</summary>
    public event Action<int>? NavigateRequested;

    /// <summary>Raised when the user presses P or Esc here, so the owner can treat it like its own toggle.</summary>
    public event Action? CloseRequested;

    /// <summary>The file currently shown, or null.</summary>
    public string? CurrentPath => _path;

    public async void ShowFile(string? path)
    {
        if (path is not null && path == _path)
            return;

        int request = ResetContent();
        _path = path;

        if (path is null)
        {
            Title = "Preview";
            FileNameText.Text = "No file selected";
            InfoText.Text = "";
            OpenButton.IsEnabled = false;
            return;
        }

        var name = Path.GetFileName(path);
        Title = $"Preview – {name}";
        FileNameText.Text = name;
        OpenButton.IsEnabled = true;

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            InfoText.Text = "";
            ShowMessage("This file no longer exists.", centered: true);
            return;
        }
        _baseInfo = $"{FormatSize(info.Length)} · Modified {info.LastWriteTime:g}";
        InfoText.Text = _baseInfo;

        try
        {
            var kind = await Task.Run(() => PreviewClassifier.Classify(path));
            if (request != _request)
                return;

            switch (kind)
            {
                case PreviewKind.Image:
                    await ShowImageAsync(path, request);
                    break;
                case PreviewKind.Video:
                case PreviewKind.Audio:
                    ShowMedia(path, kind == PreviewKind.Video);
                    break;
                case PreviewKind.Text:
                    await ShowTextAsync(path, request);
                    break;
                default:
                    await ShowThumbnailAsync(path, request, "No preview for this file type. Click Open to view it in its default app.");
                    break;
            }
        }
        catch (Exception ex)
        {
            // async void: never let a failed preview take the app down.
            if (request == _request)
                ShowMessage($"Couldn't preview this file: {ex.Message}", centered: true);
        }
    }

    /// <summary>Stops playback and lets go of the current file (call before moving files).</summary>
    public void Clear()
    {
        ResetContent();
        _path = null;
    }

    /// <summary>Lets the window really close (otherwise closing just hides it for reuse).</summary>
    public void CloseForGood()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_allowClose)
        {
            Clear();
            return;
        }
        // The title-bar X hides the window so its size and position are kept for next time.
        e.Cancel = true;
        CloseRequested?.Invoke();
    }

    /// <summary>Hides every content view and stops media. Returns the new request number.</summary>
    private int ResetContent()
    {
        int request = ++_request;

        _positionTimer.Stop();
        MediaView.Stop();
        MediaView.Source = null;
        MediaView.Close();
        _isPlaying = false;

        ImageView.Source = null;
        TextView.Text = "";
        foreach (var element in new UIElement[] { ImageView, MediaView, AudioGlyph, TextView, MessageText, MediaControls, FooterText })
            element.Visibility = Visibility.Collapsed;
        return request;
    }

    // ---- Images ----

    private async Task ShowImageAsync(string path, int request)
    {
        LoadedImage? image = null;
        try
        {
            image = await Task.Run(() => LoadImage(path));
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            // Unsupported codec or a damaged file: fall through to the thumbnail.
        }
        if (request != _request)
            return;

        if (image is null)
        {
            await ShowThumbnailAsync(path, request, "Windows can't decode this image here.");
            return;
        }
        InfoText.Text = $"{_baseInfo} · {image.Width} × {image.Height}";
        ImageView.Source = image.Bitmap;
        ImageView.Visibility = Visibility.Visible;
    }

    private sealed record LoadedImage(BitmapSource Bitmap, int Width, int Height);

    private static LoadedImage LoadImage(string path)
    {
        // Read the whole file into memory so the file is never held open (and can be moved at any time).
        var bytes = ReadAllBytesShared(path);

        int width, height;
        ushort orientation = 1;
        using (var probe = new MemoryStream(bytes, writable: false))
        {
            var frame = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            width = frame.PixelWidth;
            height = frame.PixelHeight;
            orientation = ReadOrientation(frame);
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.StreamSource = new MemoryStream(bytes, writable: false);
        if (width >= height && width > MaxImageSide)
            bitmap.DecodePixelWidth = MaxImageSide;
        else if (height > width && height > MaxImageSide)
            bitmap.DecodePixelHeight = MaxImageSide;
        bitmap.EndInit();
        bitmap.Freeze();

        // Phone photos are often stored sideways with an EXIF orientation tag; WPF doesn't apply it on its own.
        BitmapSource result = bitmap;
        double angle = orientation switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
        if (angle != 0)
        {
            result = new TransformedBitmap(bitmap, new RotateTransform(angle));
            result.Freeze();
            if (angle != 180)
                (width, height) = (height, width);
        }
        return new LoadedImage(result, width, height);
    }

    private static ushort ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.ContainsQuery("System.Photo.Orientation")
                && metadata.GetQuery("System.Photo.Orientation") is ushort value)
                return value;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            // Formats without metadata support (BMP, GIF, ICO, …).
        }
        return 1;
    }

    private static byte[] ReadAllBytesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    // ---- Video / audio ----

    private void ShowMedia(string path, bool isVideo)
    {
        MediaView.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        AudioGlyph.Visibility = isVideo ? Visibility.Collapsed : Visibility.Visible;
        MediaControls.Visibility = Visibility.Visible;
        SetSlider(0, 1);
        TimeText.Text = "";

        // Video starts playing silently; audio waits for Play so a preview never starts making noise.
        MediaView.IsMuted = isVideo;
        UpdateMuteButton();
        MediaView.Source = new Uri(path);
        if (isVideo)
            Play();
        else
            Pause(); // opens the file and reads its duration without playing
    }

    private void MediaView_MediaOpened(object sender, RoutedEventArgs e)
    {
        var parts = new List<string> { _baseInfo };
        if (MediaView.NaturalDuration.HasTimeSpan)
        {
            var duration = MediaView.NaturalDuration.TimeSpan;
            SetSlider(0, Math.Max(duration.TotalSeconds, 0.1));
            parts.Add(FormatTime(duration));
        }
        if (MediaView.HasVideo && MediaView.NaturalVideoWidth > 0)
            parts.Add($"{MediaView.NaturalVideoWidth} × {MediaView.NaturalVideoHeight}");
        InfoText.Text = string.Join(" · ", parts);
        UpdatePosition();
    }

    private void MediaView_MediaEnded(object sender, RoutedEventArgs e)
    {
        Pause();
        MediaView.Position = TimeSpan.Zero;
        UpdatePosition();
    }

    private async void MediaView_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        // Ignore a late failure from a file that is no longer shown.
        var path = _path;
        if (path is null || MediaControls.Visibility != Visibility.Visible)
            return;
        int request = ResetContent();
        await ShowThumbnailAsync(path, request, "This format can't be played here. Click Open to play it in its default app.");
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying)
            Pause();
        else
            Play();
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        MediaView.IsMuted = !MediaView.IsMuted;
        UpdateMuteButton();
    }

    private void Play()
    {
        MediaView.Play();
        _isPlaying = true;
        PlayPauseButton.Content = PauseGlyph;
        PlayPauseButton.ToolTip = "Pause";
        _positionTimer.Start();
    }

    private void Pause()
    {
        MediaView.Pause();
        _isPlaying = false;
        PlayPauseButton.Content = PlayGlyph;
        PlayPauseButton.ToolTip = "Play";
        _positionTimer.Stop();
    }

    private void UpdateMuteButton()
    {
        MuteButton.Content = MediaView.IsMuted ? MuteGlyph : VolumeGlyph;
        MuteButton.ToolTip = MediaView.IsMuted ? "Unmute" : "Mute";
    }

    private void UpdatePosition()
    {
        if (PositionSlider.IsMouseCaptureWithin)
            return; // the user is dragging the thumb
        var position = MediaView.Position;
        SetSlider(position.TotalSeconds, PositionSlider.Maximum);
        TimeText.Text = MediaView.NaturalDuration.HasTimeSpan
            ? $"{FormatTime(position)} / {FormatTime(MediaView.NaturalDuration.TimeSpan)}"
            : FormatTime(position);
    }

    private void SetSlider(double value, double maximum)
    {
        _settingSlider = true;
        PositionSlider.Maximum = maximum;
        PositionSlider.Value = Math.Min(value, maximum);
        _settingSlider = false;
    }

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settingSlider || MediaControls.Visibility != Visibility.Visible)
            return;
        MediaView.Position = TimeSpan.FromSeconds(e.NewValue);
        if (!_isPlaying)
            UpdatePosition();
    }

    // ---- Text ----

    private async Task ShowTextAsync(string path, int request)
    {
        var result = await Task.Run(() => PreviewText.Read(path));
        if (request != _request)
            return;
        TextView.Text = result.Text;
        TextView.ScrollToHome();
        TextView.Visibility = Visibility.Visible;
        if (result.Truncated)
            ShowFooter($"Showing the first {FormatSize(PreviewText.DefaultMaxBytes)}. Click Open to see the whole file.");
    }

    // ---- Everything else ----

    private async Task ShowThumbnailAsync(string path, int request, string message)
    {
        var thumbnail = await ShellThumbnail.GetAsync(path, ThumbnailSize);
        if (request != _request)
            return;
        if (thumbnail is not null)
        {
            ImageView.Source = thumbnail;
            ImageView.Visibility = Visibility.Visible;
        }
        ShowMessage(message, centered: thumbnail is null);
    }

    private void ShowMessage(string message, bool centered)
    {
        MessageText.Text = message;
        MessageText.VerticalAlignment = centered ? VerticalAlignment.Center : VerticalAlignment.Bottom;
        MessageText.Visibility = Visibility.Visible;
    }

    private void ShowFooter(string text)
    {
        FooterText.Text = text;
        FooterText.Visibility = Visibility.Visible;
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_path is not null)
            ShellActions.Open(_path, this);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None)
            return;
        switch (e.Key)
        {
            case Key.Escape:
            case Key.P:
                CloseRequested?.Invoke();
                e.Handled = true;
                break;
            // Arrow keys still scroll the text view and move the slider when those have focus.
            case Key.Up when e.OriginalSource is not TextBox and not RangeBase:
                NavigateRequested?.Invoke(-1);
                e.Handled = true;
                break;
            case Key.Down when e.OriginalSource is not TextBox and not RangeBase:
                NavigateRequested?.Invoke(+1);
                e.Handled = true;
                break;
            case Key.Space when e.OriginalSource is not ButtonBase && MediaControls.Visibility == Visibility.Visible:
                PlayPauseButton_Click(this, e);
                e.Handled = true;
                break;
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} bytes" : $"{size:0.#} {units[unit]}";
    }

    private static string FormatTime(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
}
