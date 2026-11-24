// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices.WindowsRuntime;
using EncoDeck.Platform.Media;
using EncoDeck.Platform.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace EncoDeck.App.Controls;

/// <summary>
/// HeyMelody's 3D device model. Either a 360° turntable stored as an animated WebP whose frames are
/// evenly spaced viewing angles — drag horizontally (or use the arrow keys) to rotate; it plays
/// continuously by default, using the original WebP timing; each angle is decoded at display
/// resolution, with a small cache — or a real 3D scene (<c>.vfxms</c>), shown by
/// <see cref="DeviceSceneView"/>. Without a model, or until the scene is ready, it shows the still
/// product image.
/// </summary>
public sealed partial class DeviceModelView : UserControl
{
    public static readonly DependencyProperty IsHeadphonesProperty = DependencyProperty.Register(
        nameof(IsHeadphones), typeof(bool), typeof(DeviceModelView), new PropertyMetadata(false, (d, e) => ((DeviceModelView)d).Still.IsHeadphones = (bool)e.NewValue));

    /// <summary>Selects the headphones illustration when no product image is available.</summary>
    public bool IsHeadphones
    {
        get => (bool)GetValue(IsHeadphonesProperty);
        set => SetValue(IsHeadphonesProperty, value);
    }

    public static readonly DependencyProperty ModelPathProperty = DependencyProperty.Register(
        nameof(ModelPath), typeof(string), typeof(DeviceModelView), new PropertyMetadata(null, (d, _) => ((DeviceModelView)d).OnModelPathChanged()));

    public static readonly DependencyProperty ImagePathProperty = DependencyProperty.Register(
        nameof(ImagePath), typeof(string), typeof(DeviceModelView), new PropertyMetadata(null, (d, _) => ((DeviceModelView)d).Still.SourcePath = ((DeviceModelView)d).ImagePath));

    /// <summary>Frames kept when not every angle fits <see cref="KeepAllBudgetBytes"/>.</summary>
    private const int CacheCapacity = 24;
    /// <summary>How many frames ahead of playback are decoded in advance.</summary>
    private const int PrefetchFrames = 12;
    /// <summary>Decoded pixels kept for a whole turn at most (e.g. 180 angles of about 420 x 420).</summary>
    private const long KeepAllBudgetBytes = 128L * 1024 * 1024;

    /// <summary>Decoded BGRA pixels per angle (shared with the background decoder; lock on it).</summary>
    private readonly Dictionary<int, byte[]> _cache = [];
    private readonly LinkedList<int> _recent = new();
    private readonly SemaphoreSlim _decoderWake = new(0);
    private CancellationTokenSource? _decoderCancel;
    private WriteableBitmap? _surface;
    private bool _keepAll;
    /// <summary>Direction of travel (+1 / -1), so prefetching follows a drag backwards too.</summary>
    private int _direction = 1;
    private AnimatedWebp? _model;
    /// <summary>The model path being loaded or shown; a newer request supersedes an older load.</summary>
    private string? _requestedPath;
    private TurntableMotion? _motion;
    private int _loadVersion;
    private int _cacheVersion;
    private int _shown = -1;
    private int _wanted = -1;
    private volatile bool _decoding;
    private double _decodeScale;
    private uint _pointerId;
    private bool _dragging;
    private double _dragStartX;
    private DispatcherQueueTimer? _animation;
    private long _lastTick;
    /// <summary>
    /// Playback is wanted (a model is shown and, without <see cref="Loop"/>, not finished) and not paused by the host.
    /// The timer itself also stops while there is nothing to draw (window hidden, control collapsed) or nothing moves
    /// (Windows animations off and no drag easing out); <see cref="Wake"/> restarts it.
    /// </summary>
    private bool _playing;
    /// <summary>When the shown angle last changed or the user last touched it (Environment.TickCount64).</summary>
    private long _lastMotion;
    private XamlRoot? _root;
    public bool Loop { get; set; } = true;
    private double _playbackElapsed;
    internal event EventHandler<bool>? ModelAvailabilityChanged;

    /// <summary>With animations off, the turntable only moves while a drag eases out (about three seconds); it rests after this.</summary>
    private const long RestAfterMs = 4000;

    public void PauseAnimation()
    {
        _playing = false;
        _animation?.Stop();
    }

    public void ResumeAnimation()
    {
        if (_model is not null && (Loop || _playbackElapsed < _model.Frames.Sum(f => f.DurationMs) / 1000.0)) StartPlayback();
    }

    public DeviceModelView()
    {
        InitializeComponent();
        // Reloaded (e.g. navigating back to the page): load the model and resume playback.
        Loaded += (_, _) =>
        {
            WatchRoot(XamlRoot);
            if (_model is null && ModelPath is { Length: > 0 })
            {
                _requestedPath = null;
                OnModelPathChanged();
            }
        };
        Unloaded += (_, _) =>
        {
            WatchRoot(null);
            Release();
        };
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => Wake());
        SizeChanged += (_, _) => OnSizeChanged();
        KeyDown += OnKeyDown;
        Scene.Ready += (_, _) => RevealScene();
        Scene.Failed += (_, _) =>
        {
            Scene.ScenePath = null;
            Scene.Visibility = Visibility.Collapsed;
            Still.Visibility = Visibility.Visible;
        };
    }

    /// <summary>The first 3D frame is up: cross-fade from the still image, as HeyMelody does (600 ms).</summary>
    private void RevealScene()
    {
        if (Scene.Visibility != Visibility.Visible) return;
        if (!ShellEnvironment.AnimationsEnabled())
        {
            Scene.Opacity = 1;
            Still.Visibility = Visibility.Collapsed;
            return;
        }
        var fade = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var sceneIn = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(600) };
        var stillOut = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { From = 1, To = 0, Duration = TimeSpan.FromMilliseconds(600) };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(sceneIn, Scene);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(sceneIn, "Opacity");
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(stillOut, Still);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(stillOut, "Opacity");
        fade.Children.Add(sceneIn);
        fade.Children.Add(stillOut);
        fade.Completed += (_, _) =>
        {
            if (Scene.Visibility != Visibility.Visible) return;
            Still.Visibility = Visibility.Collapsed;
            Still.Opacity = 1;
        };
        fade.Begin();
    }

    /// <summary>Path of the turntable (animated WebP) or 3D scene (<c>.vfxms</c>).</summary>
    public string? ModelPath
    {
        get => (string?)GetValue(ModelPathProperty);
        set => SetValue(ModelPathProperty, value);
    }

    /// <summary>Diagnostics: what the control is showing.</summary>
    internal string DiagnosticState =>
        Scene.Visibility == Visibility.Visible
            ? $"{Scene.DiagnosticState} still={Still.Visibility}"
            : $"model={(_model is null ? "none" : $"{_model.Frames.Count} frames {_model.CanvasWidth}x{_model.CanvasHeight}")} shown={_shown} cache={_cache.Count} keepAll={_keepAll} still={Still.Visibility}";

    /// <summary>Still image used when there is no turntable.</summary>
    public string? ImagePath
    {
        get => (string?)GetValue(ImagePathProperty);
        set => SetValue(ImagePathProperty, value);
    }

    private async void OnModelPathChanged()
    {
        string? path = ModelPath is { Length: > 0 } p ? p : null;
        if (path == _requestedPath) return;
        _requestedPath = path;
        if (path is not null && Platform.Media.Scene3D.VfxScene.IsSceneFile(path))
        {
            ShowScene(path);
            return;
        }
        HideScene();
        int version = ++_loadVersion;
        AnimatedWebp? model = null;
        if (path is not null)
        {
            try
            {
                model = File.Exists(path) ? AnimatedWebp.TryParse(await File.ReadAllBytesAsync(path)) : null;
            }
            catch (IOException)
            {
            }
        }
        if (path != _requestedPath || version != _loadVersion || !IsLoaded) return; // a newer path arrived while this one was loading

        Release();
        _model = model is { FramesAreIndependent: true } ? model : null;
        bool hasModel = _model is not null;
        ModelBox.Visibility = hasModel ? Visibility.Visible : Visibility.Collapsed;
        Still.Visibility = hasModel ? Visibility.Collapsed : Visibility.Visible;
        ModelAvailabilityChanged?.Invoke(this, hasModel);
        ProtectedCursor = hasModel ? InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast) : null;
        if (_model is null) return;

        ModelCanvas.Width = _model.CanvasWidth;
        ModelCanvas.Height = _model.CanvasHeight;
        _motion = new TurntableMotion(_model.Frames.Select(f => f.DurationMs).ToArray());
        _playbackElapsed = 0;
        PlanCache(_model);
        _decoderCancel = new CancellationTokenSource();
        var loaded = _model;
        var token = _decoderCancel.Token;
        _ = Task.Run(() => DecodeLoopAsync(loaded, token));
        ShowFrame(0);
        StartPlayback();
    }

    private void ShowScene(string path)
    {
        Release();
        ModelBox.Visibility = Visibility.Collapsed;
        ProtectedCursor = null;
        // No placeholder while the scene loads: the model fades in on its own (the still returns on failure).
        Still.Visibility = Visibility.Collapsed;
        Still.Opacity = 1;
        Scene.Opacity = 0;
        Scene.Visibility = Visibility.Visible;
        Scene.ScenePath = path;
    }

    private void HideScene()
    {
        if (Scene.Visibility == Visibility.Collapsed && Scene.ScenePath is null) return;
        Scene.ScenePath = null;
        Scene.Visibility = Visibility.Collapsed;
        Still.Opacity = 1;
    }

    // ---- rotation --------------------------------------------------------------------------------

    private void StartPlayback()
    {
        _animation ??= DispatcherQueue.CreateTimer();
        _animation.Stop();
        _animation.Interval = TimeSpan.FromMilliseconds(16);
        _animation.Tick -= OnAnimationTick;
        _animation.Tick += OnAnimationTick;
        _playing = true;
        _lastTick = _lastMotion = Environment.TickCount64;
        _animation.Start();
    }

    /// <summary>
    /// Input, or the window or control shown again: restart the timer if playback is wanted. Time spent stopped is not
    /// played back (the motion caps each step anyway), as it was not while ticks were skipped before.
    /// </summary>
    private void Wake()
    {
        if (!_playing || _animation is null || _model is null) return;
        _lastMotion = Environment.TickCount64;
        if (_animation.IsRunning) return;
        _lastTick = _lastMotion;
        _animation.Start();
    }

    private void WatchRoot(XamlRoot? root)
    {
        if (ReferenceEquals(root, _root)) return;
        if (_root is not null) _root.Changed -= OnRootChanged;
        _root = root;
        if (_root is not null) _root.Changed += OnRootChanged;
    }

    /// <summary>The window was shown (restored, or opened from the tray), resized or moved to another display scale.</summary>
    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (sender.IsHostVisible) Wake();
    }

    private void OnAnimationTick(DispatcherQueueTimer timer, object args)
    {
        if (_model is null || _motion is null || !IsLoaded) { _playing = false; timer.Stop(); return; }
        long now = Environment.TickCount64;
        double elapsed = (now - _lastTick) / 1000.0;
        _lastTick = now;
        // Nothing to draw while the window is minimised or hidden in the tray: stop until it shows (OnRootChanged) or
        // the control becomes visible again.
        if (Visibility != Visibility.Visible || XamlRoot?.IsHostVisible == false)
        {
            timer.Stop();
            return;
        }
        bool animate = ShellEnvironment.AnimationsEnabled();
        if (!Loop)
        {
            _playbackElapsed += elapsed;
            if (!animate || _playbackElapsed >= _model.Frames.Sum(f => f.DurationMs) / 1000.0)
            {
                ShowFrame(_model.Frames.Count - 1);
                _playing = false;
                timer.Stop();
                return;
            }
        }
        int before = _motion.Frame;
        _motion.Advance(elapsed, animate);
        int after = _motion.Frame;
        if (after != before) _direction = Step(before, after, _model.Frames.Count);
        if (after != _shown) ShowFrame(after);
        if (after != before || _dragging) _lastMotion = now;
        // Windows animations off: only a drag moves it (and eases out for about three seconds). At rest, stop ticking
        // until the next input (Wake).
        else if (!animate && now - _lastMotion > RestAfterMs) timer.Stop();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_model is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !Root.CapturePointer(e.Pointer)) return;
        _motion?.BeginDrag();
        _dragging = true;
        Wake();
        _pointerId = e.Pointer.PointerId;
        _dragStartX = e.GetCurrentPoint(this).Position.X;
        Focus(FocusState.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || _model is null || e.Pointer.PointerId != _pointerId) return;
        double dx = e.GetCurrentPoint(this).Position.X - _dragStartX;
        RotateDrag(dx);
        e.Handled = true;
    }

    private void RotateDrag(double dx)
    {
        if (_model is null || _motion is null) return;
        int before = _motion.Frame;
        _motion.DragTo(dx, ActualWidth, ShellEnvironment.AnimationsEnabled());
        if (_motion.Frame != before) _direction = Step(before, _motion.Frame, _model.Frames.Count);
        ShowFrame(_motion.Frame);
    }

    /// <summary>+1 or -1: the shorter way round from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static int Step(int from, int to, int count)
    {
        int forward = ((to - from) % count + count) % count;
        return forward <= count / 2 ? 1 : -1;
    }

    /// <summary>The 3D scene's self-test (see <see cref="DeviceSceneView"/>).</summary>
    internal Task ExerciseSceneForDiagnosticsAsync(string output, Action<string> progress) =>
        Scene.ExerciseForDiagnosticsAsync(output, progress);

    /// <summary>Exercises the same drag/decoding/cache path in an off-screen diagnostic window.</summary>
    internal async Task ExerciseRotationForDiagnosticsAsync(Action<string> progress)
    {
        async Task WaitForFrame()
        {
            long deadline = Environment.TickCount64 + 10_000;
            while (_shown != _wanted || _decoding || _model is null)
            {
                if (Environment.TickCount64 > deadline) throw new TimeoutException(DiagnosticState);
                await Task.Delay(10);
            }
        }
        long loadDeadline = Environment.TickCount64 + 10_000;
        while (_model is null) { if (Environment.TickCount64 > loadDeadline) throw new TimeoutException(DiagnosticState); await Task.Delay(10); }
        _animation?.Stop();
        await WaitForFrame();
        _dragging = true;
        _motion!.BeginDrag();
        progress("rotating through cache eviction");
        for (int i = 0; i < 240; i++)
        {
            RotateDrag(i * Math.Max(1, ActualWidth) / 90);
            for (int step = 0; step < 12; step++) _motion!.Advance(0.1);
            ShowFrame(_motion!.Frame);
            await WaitForFrame();
        }
        int cached;
        lock (_cache) cached = _cache.Count;
        if (!_keepAll && cached > CacheCapacity) throw new InvalidOperationException("Frame cache exceeded its bound.");
        progress("clearing displayed frame and resizing");
        ClearCache();
        ShowFrame(30 % _model!.Frames.Count);
        await WaitForFrame();
        Width = 420;
        await Task.Delay(100);
        for (int i = 0; i < 300; i++) RotateDrag(i * 7);
        for (int step = 0; step < 12; step++) _motion!.Advance(0.1);
        ShowFrame(_motion!.Frame);
        await WaitForFrame();
        _dragging = false;
        _motion.EndDrag();
        int beforePlayback = _motion.Frame;
        for (int i = 0; i < 45; i++) _motion.Advance(0.1);
        if (beforePlayback == _motion.Frame) throw new InvalidOperationException("Playback did not resume after dragging.");
        ShowFrame(_motion.Frame);
        await WaitForFrame();
        progress("rotation completed");
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || e.Pointer.PointerId != _pointerId) return;
        _dragging = false;
        _motion?.EndDrag();
        Root.ReleasePointerCapture(e.Pointer);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_model is null || e.Key is not (Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right)) return;
        _motion?.Step(e.Key == Windows.System.VirtualKey.Right ? 5 : -5);
        if (_motion is not null) ShowFrame(_motion.Frame);
        Wake();
        e.Handled = true;
    }

    // ---- decoding --------------------------------------------------------------------------------
    //
    // Frames are decoded on a background thread, ahead of playback, at the size they are drawn;
    // the UI thread only copies the ready pixels into one reused bitmap. When every angle fits in
    // the memory budget they are all kept, so after one turn nothing is decoded any more.

    private void ShowFrame(int index)
    {
        if (_model is not { } model || index < 0 || index >= model.Frames.Count) return;
        _wanted = index;
        byte[]? pixels;
        lock (_cache) pixels = _cache.TryGetValue(index, out var cached) ? cached : null;
        if (pixels is not null)
        {
            Apply(index, pixels);
            Touch(index);
        }
        _decoderWake.Release();
    }

    /// <summary>Background decoder: the wanted angle first, then the next ones in the direction of travel.</summary>
    private async Task DecodeLoopAsync(AnimatedWebp model, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _decoderWake.WaitAsync(cancellationToken).ConfigureAwait(false);
                while (_decoderWake.CurrentCount > 0) _decoderWake.Wait(0, CancellationToken.None);
                while (!cancellationToken.IsCancellationRequested && NextToDecode(model) is { } next)
                {
                    var (index, scale, version) = next;
                    _decoding = true;
                    var pixels = await DecodeFrameAsync(model, index, scale).ConfigureAwait(false);
                    _decoding = false;
                    if (pixels is null || version != Volatile.Read(ref _cacheVersion)) continue;
                    Remember(index, pixels);
                    if (Volatile.Read(ref _wanted) == index) DispatcherQueue.TryEnqueue(DispatcherQueuePriority.High, () => ShowReady(model, index));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _decoding = false;
        }
    }

    private void ShowReady(AnimatedWebp model, int index)
    {
        if (!ReferenceEquals(model, _model) || _wanted != index) return;
        ShowFrame(index);
    }

    /// <summary>The wanted frame if missing, else the first missing frame of the prefetch window.</summary>
    private (int Index, double Scale, int Version)? NextToDecode(AnimatedWebp model)
    {
        int wanted = Volatile.Read(ref _wanted);
        if (wanted < 0) return null;
        int count = model.Frames.Count;
        int direction = Volatile.Read(ref _direction) < 0 ? -1 : 1;
        int window = _keepAll ? count : PrefetchFrames;
        lock (_cache)
        {
            for (int i = 0; i < window; i++)
            {
                int index = ((wanted + direction * i) % count + count) % count;
                if (!_cache.ContainsKey(index)) return (index, _decodeScale, _cacheVersion);
            }
        }
        return null;
    }

    private static async Task<byte[]?> DecodeFrameAsync(AnimatedWebp model, int index, double scale)
    {
        try
        {
            var frame = model.Frames[index];
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(model.ToStillWebp(index).AsBuffer()).AsTask().ConfigureAwait(false);
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask().ConfigureAwait(false);
            var transform = new BitmapTransform
            {
                ScaledWidth = ScaledSize(frame.Width, scale),
                ScaledHeight = ScaledSize(frame.Height, scale),
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            var data = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask().ConfigureAwait(false);
            return data.DetachPixelData();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // A frame Windows cannot decode: keep showing the last good one.
            return null;
        }
    }

    private static uint ScaledSize(int size, double scale) => (uint)Math.Max(1, Math.Round(size * scale));

    /// <summary>Pixels per model unit, so frames are decoded at exactly the size they are drawn.</summary>
    private double DecodeScale(AnimatedWebp model)
    {
        double fit = Math.Min(ActualWidth / model.CanvasWidth, ActualHeight / model.CanvasHeight);
        double raster = XamlRoot?.RasterizationScale ?? 1;
        return Math.Clamp(fit * raster, 0.1, 1);
    }

    /// <summary>Keeps every angle when they fit the budget; otherwise a window ahead of playback.</summary>
    private void PlanCache(AnimatedWebp model)
    {
        _decodeScale = DecodeScale(model);
        long bytes = model.Frames.Sum(f => (long)ScaledSize(f.Width, _decodeScale) * ScaledSize(f.Height, _decodeScale) * 4);
        _keepAll = bytes <= KeepAllBudgetBytes;
    }

    private void OnSizeChanged()
    {
        if (_model is null) return;
        double fit = DecodeScale(_model);
        // Re-decode at the new size when it changed noticeably.
        if (Math.Abs(fit - _decodeScale) / Math.Max(_decodeScale, 0.01) > 0.15)
        {
            ClearCache();
            PlanCache(_model);
            if (_shown >= 0) ShowFrame(_shown);
        }
    }

    /// <summary>Copies the pixels into the reused surface (a new one only when the frame size changes).</summary>
    private void Apply(int index, byte[] pixels)
    {
        var frame = _model!.Frames[index];
        int width = (int)ScaledSize(frame.Width, _decodeScale), height = (int)ScaledSize(frame.Height, _decodeScale);
        if (pixels.Length != width * height * 4) return; // decoded for an earlier size
        if (_surface is null || _surface.PixelWidth != width || _surface.PixelHeight != height)
        {
            _surface = new WriteableBitmap(width, height);
            FrameImage.Source = _surface;
        }
        pixels.CopyTo(0, _surface.PixelBuffer, 0, pixels.Length);
        _surface.Invalidate();
        Canvas.SetLeft(FrameImage, frame.X);
        Canvas.SetTop(FrameImage, frame.Y);
        FrameImage.Width = frame.Width;
        FrameImage.Height = frame.Height;
        _shown = index;
    }

    private void Remember(int index, byte[] pixels)
    {
        lock (_cache)
        {
            _cache[index] = pixels;
            _recent.Remove(index);
            _recent.AddFirst(index);
            int capacity = _keepAll ? int.MaxValue : CacheCapacity;
            int shown = Volatile.Read(ref _shown);
            while (_cache.Count > capacity && _recent.Last is { } oldest)
            {
                var victim = oldest.Value == shown ? oldest.Previous : oldest;
                if (victim is null) break;
                _recent.Remove(victim);
                _cache.Remove(victim.Value);
            }
        }
    }

    private void Touch(int index)
    {
        lock (_cache)
        {
            _recent.Remove(index);
            _recent.AddFirst(index);
        }
    }

    private void ClearCache()
    {
        Interlocked.Increment(ref _cacheVersion);
        lock (_cache)
        {
            _cache.Clear();
            _recent.Clear();
        }
    }

    private void Release()
    {
        _loadVersion++;
        _playing = false;
        _animation?.Stop();
        _decoderCancel?.Cancel();
        _decoderCancel = null;
        _dragging = false;
        _motion = null;
        Root.ReleasePointerCaptures();
        ClearCache();
        FrameImage.Source = null;
        _surface = null;
        _model = null;
        _shown = -1;
        _wanted = -1;
    }
}
