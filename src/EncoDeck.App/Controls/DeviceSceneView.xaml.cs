// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using EncoDeck.Platform.Media.Scene3D;
using EncoDeck.Platform.Shell;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EncoDeck.App.Controls;

/// <summary>
/// HeyMelody's interactive 3D device model (<c>.vfxms</c>): loaded and parsed like HeyMelody's
/// <c>ModelScene</c>, drawn by <see cref="SceneRenderer"/> and moved by HeyMelody's camera
/// (<see cref="OrbitCamera"/>). Drag in any direction to turn it, use the wheel to zoom, double-click
/// to reset; arrow keys turn it too. It turns slowly on its own unless Windows animations are off.
/// <para>
/// Frames are drawn only while the view moves and the window is visible: otherwise the control leaves
/// <see cref="CompositionTarget.Rendering"/> altogether until input, a size or scale change, or the window
/// showing again wakes it. Plain idle turning is drawn at about 30 frames a second (same speed in degrees
/// per second), input and coasting at up to 60. Everything (device, textures, bitmap) is released when the
/// control unloads.
/// </para>
/// </summary>
public sealed partial class DeviceSceneView : UserControl
{
    public static readonly DependencyProperty ScenePathProperty = DependencyProperty.Register(
        nameof(ScenePath), typeof(string), typeof(DeviceSceneView), new PropertyMetadata(null, (d, _) => ((DeviceSceneView)d).OnScenePathChanged()));

    private readonly OrbitCamera _camera = new();
    private readonly Stopwatch _clock = new();
    private SceneRenderer? _renderer;
    private BitmapTarget? _target;
    private WriteableBitmap? _bitmap;
    private XamlRoot? _root;
    private string? _loadedPath;
    private int _loadVersion;
    private bool _rendering;
    private bool _dirty;
    private bool _announced;
    private TimeSpan _lastFrame;
    private uint? _pointerId;
    private Windows.Foundation.Point _lastPoint;

    public DeviceSceneView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            WatchRoot(XamlRoot);
            if (_renderer is null && ScenePath is { Length: > 0 }) OnScenePathChanged();
        };
        Unloaded += (_, _) =>
        {
            WatchRoot(null);
            Release();
        };
        KeyDown += OnKeyDown;
        // A new size needs a new bitmap and a frame even while the camera rests.
        SizeChanged += (_, _) => Wake();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => Wake());
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    }

    /// <summary>Path of the <c>.vfxms</c> scene.</summary>
    public string? ScenePath
    {
        get => (string?)GetValue(ScenePathProperty);
        set => SetValue(ScenePathProperty, value);
    }

    /// <summary>The scene could not be loaded or drawn; the host shows its still image instead.</summary>
    public event EventHandler<Exception>? Failed;

    /// <summary>The first frame of a scene was presented.</summary>
    public event EventHandler? Ready;

    /// <summary>Diagnostics: what the control is showing.</summary>
    internal string DiagnosticState =>
        $"scene={(_renderer?.HasScene == true ? Path.GetFileName(_loadedPath) : "none")} target={_target?.Width}x{_target?.Height} msaa={_renderer?.SampleCount} camera={_camera.Current} yaw={_camera.Yaw:0} pitch={_camera.Pitch:0} distance={_camera.Distance:0.00}";

    private async void OnScenePathChanged()
    {
        string? path = ScenePath is { Length: > 0 } p ? p : null;
        if (path == _loadedPath && _renderer is not null) return;
        Release();
        int version = _loadVersion;
        if (path is null || !IsLoaded) return;
        _loadedPath = path;
        SceneRenderer renderer;
        try
        {
            // Parsing, texture decoding, device creation and upload all happen off the UI thread.
            renderer = await Task.Run(async () =>
            {
                var assets = await SceneAssets.LoadAsync(path).ConfigureAwait(false);
                var created = SceneRenderer.Create();
                try
                {
                    created.Load(assets);
                    return created;
                }
                catch
                {
                    created.Dispose();
                    throw;
                }
            });
        }
        catch (Exception ex) when (IsSceneError(ex))
        {
            if (version == _loadVersion)
            {
                _loadedPath = null;
                Failed?.Invoke(this, ex);
            }
            return;
        }
        if (version != _loadVersion || !IsLoaded)
        {
            renderer.Dispose();
            return;
        }
        _renderer = renderer;
        _camera.ResetImmediately();
        _camera.AutoRotate = ShellEnvironment.AnimationsEnabled();
        _announced = false;
        try
        {
            CreateTarget();
        }
        catch (Exception ex) when (IsSceneError(ex))
        {
            Release();
            Failed?.Invoke(this, ex);
            return;
        }
        StartRendering();
    }

    private static bool IsSceneError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException
        or InvalidOperationException or COMException or SharpGen.Runtime.SharpGenException or System.Text.Json.JsonException or ArgumentException;

    // ---- bitmap ----------------------------------------------------------------------------------

    // A bitmap rather than a SwapChainPanel: a swap chain's transparent pixels show the window's Mica
    // backdrop instead of the page beneath, which leaves a visibly darker box around the model.
    private (int Width, int Height) PixelSize()
    {
        double scale = XamlRoot?.RasterizationScale ?? 1;
        return ((int)Math.Ceiling(Surface.ActualWidth * scale), (int)Math.Ceiling(Surface.ActualHeight * scale));
    }

    private void CreateTarget()
    {
        if (_renderer is null) return;
        var (width, height) = PixelSize();
        _target = new BitmapTarget(_renderer, width, height);
        _dirty = true;
    }

    /// <summary>Follows the control's size and the display scale; a new bitmap is made when they change.</summary>
    private void EnsureBitmap()
    {
        var (width, height) = PixelSize();
        _target!.Resize(width, height);
        if (_bitmap is not null && _bitmap.PixelWidth == _target.Width && _bitmap.PixelHeight == _target.Height) return;
        _bitmap = new WriteableBitmap(_target.Width, _target.Height);
        Frame.Source = _bitmap;
        _dirty = true;
    }

    // ---- frames ----------------------------------------------------------------------------------

    private void StartRendering()
    {
        if (_rendering) return;
        _rendering = true;
        _clock.Restart();
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRendering()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    /// <summary>
    /// Something may need drawing again (input, size, scale, the window or the control shown again): back on
    /// <see cref="CompositionTarget.Rendering"/> while a scene is loaded. The camera's clock does not run while it is
    /// off, just as it did not while frames were skipped before.
    /// </summary>
    private void Wake()
    {
        if (_renderer is null || _target is null) return;
        StartRendering();
    }

    private void WatchRoot(XamlRoot? root)
    {
        if (ReferenceEquals(root, _root)) return;
        if (_root is not null) _root.Changed -= OnRootChanged;
        _root = root;
        if (_root is not null) _root.Changed += OnRootChanged;
    }

    /// <summary>The window was shown, resized or moved to another display scale.</summary>
    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (sender.IsHostVisible) Wake();
    }

    private void OnRendering(object? sender, object e)
    {
        if (_renderer is null || _target is null) return;
        // Nothing to draw while the window is minimised or hidden in the tray: leave Rendering until it shows again
        // (OnRootChanged) or the control is made visible.
        if (Visibility != Visibility.Visible || XamlRoot?.IsHostVisible == false)
        {
            StopRendering();
            return;
        }
        var now = _clock.Elapsed;
        float dt = (float)(now - _lastFrame).TotalSeconds;
        // At most ~60 frames a second, also on high refresh-rate displays; plain idle turning (slow and steady) at
        // about 30, which halves the GPU read-backs. The camera moves by elapsed time, so the speed stays the same.
        if (dt < (_camera.Current == OrbitCamera.State.Rotate ? 0.030f : 0.0155f)) return;
        _lastFrame = now;
        _camera.AutoRotate = ShellEnvironment.AnimationsEnabled();
        _camera.Update(dt);
        try
        {
            EnsureBitmap();
            if (!_camera.IsMoving && !_dirty)
            {
                // At rest (Windows animations off, nothing coasting): no frames until input or a change wakes it.
                // Idle is the half second before turning starts, which still needs the clock.
                if (_camera.Current != OrbitCamera.State.Idle) StopRendering();
                return;
            }
            // Straight from the mapped texture into the bitmap's own buffer.
            using (var pixels = _bitmap!.PixelBuffer.AsStream())
            {
                _target.Render(_camera, pixels);
            }
            _bitmap.Invalidate();
            _dirty = false;
            if (!_announced)
            {
                _announced = true;
                Ready?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex) when (ex is COMException or SharpGen.Runtime.SharpGenException)
        {
            RecoverDevice();
        }
    }

    /// <summary>The GPU was reset or removed (driver update, adapter change): start over with a new device.</summary>
    private void RecoverDevice()
    {
        Release();
        if (IsLoaded) OnScenePathChanged();
    }

    // ---- input (HeyMelody: one finger turns, two fingers zoom) -----------------------------------

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Surface);
        if (_renderer is null || (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed)) return;
        if (_pointerId is not null || !Surface.CapturePointer(e.Pointer)) return;
        _pointerId = e.Pointer.PointerId;
        _lastPoint = point.Position;
        _camera.BeginControl();
        Wake();
        Focus(FocusState.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerId != e.Pointer.PointerId) return;
        var position = e.GetCurrentPoint(Surface).Position;
        _camera.Slide((float)(position.X - _lastPoint.X) * OrbitCamera.TouchPixelsPerDip, (float)(position.Y - _lastPoint.Y) * OrbitCamera.TouchPixelsPerDip);
        _lastPoint = position;
        Wake();
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerId != e.Pointer.PointerId) return;
        _pointerId = null;
        _camera.EndControl();
        Wake();
        Surface.ReleasePointerCapture(e.Pointer);
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_renderer is null) return;
        int delta = e.GetCurrentPoint(Surface).Properties.MouseWheelDelta;
        // One notch is about a 40-pixel pinch on HeyMelody (wheel up = closer).
        _camera.Scroll(delta / 120f * 40);
        if (_pointerId is null) _camera.EndControl();
        Wake();
        e.Handled = true;
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_renderer is null) return;
        _camera.StartRecover();
        Wake();
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_renderer is null) return;
        (float dx, float dy) = e.Key switch
        {
            Windows.System.VirtualKey.Left => (-60f, 0f),
            Windows.System.VirtualKey.Right => (60f, 0f),
            Windows.System.VirtualKey.Up => (0f, -60f),
            Windows.System.VirtualKey.Down => (0f, 60f),
            _ => (0f, 0f),
        };
        if (dx == 0 && dy == 0) return;
        _camera.BeginControl();
        _camera.Slide(dx, dy);
        _camera.EndControl();
        Wake();
        e.Handled = true;
    }

    // ---- diagnostics ------------------------------------------------------------------------------

    /// <summary>
    /// Off-screen self-test: waits for the scene, drags, zooms and resets through the real input path,
    /// and saves the rendered views (the same device and camera, read back) as PNG files.
    /// </summary>
    internal async Task ExerciseForDiagnosticsAsync(string output, Action<string> progress)
    {
        async Task WaitFor(Func<bool> condition, string what)
        {
            long deadline = Environment.TickCount64 + 15_000;
            while (!condition())
            {
                if (Environment.TickCount64 > deadline) throw new TimeoutException($"{what}: {DiagnosticState}");
                await Task.Delay(16);
            }
        }
        async Task Save(string name)
        {
            var (width, height) = (_target!.Width, _target.Height);
            var pixels = _renderer!.RenderToPixels(width, height, _camera);
            var file = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(output).AsTask();
            var png = await file.CreateFileAsync(name + ".png", Windows.Storage.CreationCollisionOption.ReplaceExisting).AsTask();
            using var stream = await png.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite).AsTask();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream).AsTask();
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, (uint)width, (uint)height, 96, 96, pixels);
            await encoder.FlushAsync().AsTask();
            progress($"{name}: {DiagnosticState}");
        }

        await WaitFor(() => _announced, "first frame");
        await Save("1-loaded");
        float yaw = _camera.Yaw;
        await Task.Delay(1500); // 0.5 s idle, then 20°/s
        if (_camera.AutoRotate && Math.Abs(_camera.Yaw - yaw) < 5) throw new InvalidOperationException("The model does not turn by itself: " + DiagnosticState);

        // Drag right and down for half a second, like a mouse would (this bypasses the input handlers, so wake it).
        _camera.BeginControl();
        Wake();
        for (int i = 0; i < 30; i++)
        {
            _camera.Slide(8 * OrbitCamera.TouchPixelsPerDip, 4 * OrbitCamera.TouchPixelsPerDip);
            await Task.Delay(16);
        }
        _camera.EndControl();
        await WaitFor(() => _camera.Current is OrbitCamera.State.ControlOverToRotate or OrbitCamera.State.Rotate, "coasting");
        if (_camera.Pitch >= -5) throw new InvalidOperationException("Dragging down did not tilt the camera: " + DiagnosticState);
        await Save("2-dragged");

        for (int i = 0; i < 6; i++) _camera.Scroll(40);
        _camera.EndControl();
        Wake();
        await Task.Delay(800);
        if (_camera.Distance >= _camera.DefaultDistance - 0.5f) throw new InvalidOperationException("The wheel did not zoom: " + DiagnosticState);
        await Save("3-zoomed");

        _camera.StartRecover();
        Wake();
        await WaitFor(() => _camera.Current == OrbitCamera.State.Rotate, "reset");
        await Save("4-reset");
    }

    // ---- teardown --------------------------------------------------------------------------------

    private void Release()
    {
        ++_loadVersion; // An in-flight load must not revive a released or replaced scene.
        StopRendering();
        _pointerId = null;
        Surface.ReleasePointerCaptures();
        Frame.Source = null;
        _bitmap = null;
        _target?.Dispose();
        _target = null;
        _renderer?.Dispose();
        _renderer = null;
        _loadedPath = null;
        _announced = false;
    }
}
