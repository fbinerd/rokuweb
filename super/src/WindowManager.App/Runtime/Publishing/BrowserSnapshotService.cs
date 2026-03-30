using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CefSharp;
using CefSharp.Wpf;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowManager.App.Runtime;

namespace WindowManager.App.Runtime.Publishing;

public sealed class BrowserSnapshotService
{
    private static readonly StreamingTuning Tuning = StreamingTuning.Current;
    private static readonly TimeSpan CachedFrameLifetime = TimeSpan.FromMilliseconds(Tuning.SnapshotCacheLifetimeMs);
    private static readonly TimeSpan BackgroundCaptureInterval = TimeSpan.FromMilliseconds(Tuning.SnapshotBackgroundCaptureIntervalMs);
    private static readonly TimeSpan DirectVideoOverlayProbeInterval = TimeSpan.FromMilliseconds(350);
    private readonly ConcurrentDictionary<Guid, ChromiumWebBrowser> _browsers = new ConcurrentDictionary<Guid, ChromiumWebBrowser>();
    private readonly ConcurrentDictionary<Guid, CachedBitmapFrame> _cachedBitmapFrames = new ConcurrentDictionary<Guid, CachedBitmapFrame>();
    private readonly ConcurrentDictionary<Guid, CachedJpegFrame> _cachedFrames = new ConcurrentDictionary<Guid, CachedJpegFrame>();
    private readonly ConcurrentDictionary<Guid, Task<byte[]?>> _captureTasks = new ConcurrentDictionary<Guid, Task<byte[]?>>();
    private readonly ConcurrentDictionary<Guid, Task<CachedBitmapFrame?>> _bitmapCaptureTasks = new ConcurrentDictionary<Guid, Task<CachedBitmapFrame?>>();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _captureLoops = new ConcurrentDictionary<Guid, CancellationTokenSource>();
    private readonly ConcurrentDictionary<Guid, RemoteCursorState> _cursorStates = new ConcurrentDictionary<Guid, RemoteCursorState>();
    private readonly ConcurrentDictionary<Guid, BrowserDirectVideoOverlayState> _directVideoOverlayStates = new ConcurrentDictionary<Guid, BrowserDirectVideoOverlayState>();
    private readonly ConcurrentDictionary<Guid, DateTime> _lastDirectVideoOverlayProbeUtc = new ConcurrentDictionary<Guid, DateTime>();
    private readonly ConcurrentDictionary<Guid, bool> _directVideoSuppressionRequested = new ConcurrentDictionary<Guid, bool>();
    private readonly ConcurrentDictionary<Guid, string> _lastDirectVideoOverlayLog = new ConcurrentDictionary<Guid, string>();
    private readonly ConcurrentDictionary<Guid, DateTime> _lastDirectVideoSuppressionLogUtc = new ConcurrentDictionary<Guid, DateTime>();

    public void Register(Guid windowId, ChromiumWebBrowser browser)
    {
        _browsers[windowId] = browser;
        _cachedBitmapFrames.TryRemove(windowId, out _);
        _cachedFrames.TryRemove(windowId, out _);
        _directVideoOverlayStates.TryRemove(windowId, out _);
        _lastDirectVideoOverlayProbeUtc.TryRemove(windowId, out _);
        _lastDirectVideoOverlayLog.TryRemove(windowId, out _);
        _lastDirectVideoSuppressionLogUtc.TryRemove(windowId, out _);
        StartCaptureLoop(windowId, browser);
        _cursorStates.TryAdd(windowId, new RemoteCursorState());
    }

    public void Unregister(Guid windowId)
    {
        _browsers.TryRemove(windowId, out _);
        _cachedBitmapFrames.TryRemove(windowId, out _);
        _cachedFrames.TryRemove(windowId, out _);
        _bitmapCaptureTasks.TryRemove(windowId, out _);
        _captureTasks.TryRemove(windowId, out _);
        _directVideoOverlayStates.TryRemove(windowId, out _);
        _lastDirectVideoOverlayProbeUtc.TryRemove(windowId, out _);
        _directVideoSuppressionRequested.TryRemove(windowId, out _);
        _lastDirectVideoOverlayLog.TryRemove(windowId, out _);
        _lastDirectVideoSuppressionLogUtc.TryRemove(windowId, out _);
        if (_captureLoops.TryRemove(windowId, out var cancellation))
        {
            try
            {
                cancellation.Cancel();
            }
            catch
            {
            }

            cancellation.Dispose();
        }
        _cursorStates.TryRemove(windowId, out _);
    }

    public void SetDirectVideoSuppression(Guid windowId, bool enabled)
    {
        if (enabled)
        {
            _directVideoSuppressionRequested[windowId] = true;
            _ = ApplyImmediateDirectVideoSuppressionAsync(windowId);
            if (_browsers.TryGetValue(windowId, out var browser))
            {
                _ = RefreshDirectVideoOverlayStateAsync(windowId, browser, CancellationToken.None);
            }
            return;
        }

        _directVideoSuppressionRequested.TryRemove(windowId, out _);
        _ = RestoreDirectVideoPresentationAsync(windowId);
    }

    public BrowserDirectVideoOverlayState GetDirectVideoOverlayState(Guid windowId)
    {
        return _directVideoOverlayStates.TryGetValue(windowId, out var state)
            ? state
            : BrowserDirectVideoOverlayState.None;
    }

    private Task ApplyImmediateDirectVideoSuppressionAsync(Guid windowId)
    {
        if (!_browsers.TryGetValue(windowId, out var browser))
        {
            return Task.CompletedTask;
        }

        return browser.Dispatcher.InvokeAsync(async () =>
        {
            var cefBrowser = browser.GetBrowser();
            var frame = cefBrowser?.MainFrame;
            if (frame is null)
            {
                return;
            }

            const string suppressionScript = @"
(function() {
  try {
    const host = (window.location.hostname || '').toLowerCase();
    const pageUrl = (window.location.href || '').toLowerCase();
    const isYoutubePage =
      host.indexOf('youtube.com') >= 0 ||
      host.indexOf('youtu.be') >= 0 ||
      host.indexOf('youtube-nocookie.com') >= 0 ||
      pageUrl.indexOf('youtube.com') >= 0 ||
      pageUrl.indexOf('youtu.be') >= 0 ||
      pageUrl.indexOf('youtube-nocookie.com') >= 0;

    let changed = false;
    const mediaNodes = Array.from(document.querySelectorAll('video, audio'));
    mediaNodes.forEach(function(node) {
      try {
        const src = ((node.currentSrc || node.src || '') + '').toLowerCase();
        const parentFrame = node.closest('iframe');
        const isYoutubeMedia =
          isYoutubePage ||
          src.indexOf('youtube') >= 0 ||
          src.indexOf('googlevideo.com') >= 0 ||
          !!document.querySelector('.html5-video-player, .ytp-play-button, ytd-player, #movie_player');
        if (!isYoutubeMedia) return;
        if (typeof node.pause === 'function') node.pause();
        node.muted = true;
        changed = true;
      } catch (e) {
      }
    });

    document.querySelectorAll('iframe').forEach(function(node) {
      try {
        const src = ((node.getAttribute && node.getAttribute('src')) || node.src || '').toLowerCase();
        if (src.indexOf('youtube.com/embed/') >= 0 || src.indexOf('youtube-nocookie.com/embed/') >= 0) {
          node.style.visibility = 'hidden';
          node.style.pointerEvents = 'none';
          node.setAttribute('data-super-direct-overlay-hidden', '1');
          changed = true;
        }
      } catch (e) {
      }
    });

    return JSON.stringify({ ok: true, changed: changed, youtube: isYoutubePage });
  } catch (e) {
    return JSON.stringify({ ok: false, changed: false, youtube: false });
  }
})();";

            var response = await frame.EvaluateScriptAsync(suppressionScript).ConfigureAwait(true);
            var json = response?.Result as string;
            if (!string.IsNullOrWhiteSpace(json) && json.IndexOf(@"""changed"":true", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var nowUtc = DateTime.UtcNow;
                if (!_lastDirectVideoSuppressionLogUtc.TryGetValue(windowId, out var lastLogUtc) ||
                    nowUtc - lastLogUtc >= TimeSpan.FromSeconds(2))
                {
                    _lastDirectVideoSuppressionLogUtc[windowId] = nowUtc;
                    AppLog.Write("BrowserVideoBlock", string.Format("Video local pausado/bloqueado no CEF => janela={0}", windowId.ToString("N")));
                }
            }
        }).Task.Unwrap();
    }

    public bool IsRegistered(Guid windowId)
    {
        return _browsers.ContainsKey(windowId);
    }

    public void NavigateRegisteredBrowser(Guid windowId, Uri uri)
    {
        if (!_browsers.TryGetValue(windowId, out var browser))
        {
            return;
        }

        _ = browser.Dispatcher.InvokeAsync(() =>
        {
            if (!string.Equals(browser.Address, uri.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                browser.Address = uri.ToString();
            }
        });
    }

    public Task SetNavigationBarEnabledAsync(Guid windowId, bool enabled)
    {
        if (!_browsers.TryGetValue(windowId, out var browser))
        {
            return Task.CompletedTask;
        }

        return BrowserCaptureWindow.ApplyNavigationBarPreferenceAsync(browser, enabled);
    }

    public void InvalidateCapture(Guid windowId)
    {
        _cachedBitmapFrames.TryRemove(windowId, out _);
        _cachedFrames.TryRemove(windowId, out _);
    }

    public async Task<CachedBitmapFrame?> CaptureBitmapFrameAsync(Guid windowId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_browsers.TryGetValue(windowId, out var browser))
        {
            return null;
        }

        if (_cachedBitmapFrames.TryGetValue(windowId, out var cachedBitmap) &&
            cachedBitmap.Pixels is not null &&
            cachedBitmap.Pixels.Length > 0 &&
            DateTime.UtcNow - cachedBitmap.CapturedAtUtc <= CachedFrameLifetime)
        {
            return cachedBitmap;
        }

        var captureTask = _bitmapCaptureTasks.GetOrAdd(windowId, _ => CaptureFreshBitmapFrameAsync(windowId, browser));
        try
        {
            var bitmapFrame = await captureTask.ConfigureAwait(false);
            if (bitmapFrame is not null && bitmapFrame.Pixels.Length > 0)
            {
                _cachedBitmapFrames[windowId] = bitmapFrame;
            }
            return bitmapFrame;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (_bitmapCaptureTasks.TryGetValue(windowId, out var activeTask) && ReferenceEquals(activeTask, captureTask))
            {
                _bitmapCaptureTasks.TryRemove(windowId, out _);
            }
        }
    }

    public async Task<byte[]?> CaptureJpegAsync(Guid windowId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var frame = await CaptureBitmapFrameAsync(windowId, cancellationToken).ConfigureAwait(false);
        if (frame is null)
        {
            return null;
        }

        if (_cachedFrames.TryGetValue(windowId, out var cached) &&
            cached.Bytes is not null &&
            cached.Bytes.Length > 0 &&
            cached.CapturedAtUtc >= frame.CapturedAtUtc)
        {
            return cached.Bytes;
        }

        var captureTask = _captureTasks.GetOrAdd(windowId, _ => EncodeJpegAsync(windowId, frame));
        try
        {
            var jpegBytes = await captureTask.ConfigureAwait(false);
            if (jpegBytes is not null && jpegBytes.Length > 0)
            {
                _cachedFrames[windowId] = new CachedJpegFrame(jpegBytes, frame.CapturedAtUtc);
            }

            return jpegBytes;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (_captureTasks.TryGetValue(windowId, out var activeTask) && ReferenceEquals(activeTask, captureTask))
            {
                _captureTasks.TryRemove(windowId, out _);
            }
        }
    }

    public async Task<RemoteCommandResult> SendRemoteCommandAsync(Guid windowId, string command, int? x, int? y, string? text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_browsers.TryGetValue(windowId, out var browser))
        {
            AppLog.Write("RokuControl", string.Format("Browser nao encontrado para janela {0}", windowId.ToString("N")));
            return RemoteCommandResult.Failure();
        }

        return await browser.Dispatcher.InvokeAsync(async () =>
        {
            var cefBrowser = browser.GetBrowser();
            var host = cefBrowser?.GetHost();
            if (cefBrowser is null || host is null)
            {
                AppLog.Write("RokuControl", string.Format("CEF indisponivel para janela {0}", windowId.ToString("N")));
                return RemoteCommandResult.Failure();
            }

            host.SetFocus(true);
            host.SendFocusEvent(true);
            var frame = cefBrowser.MainFrame;
            var normalizedCommand = (command ?? string.Empty).Trim().ToLowerInvariant();
            var width = Math.Max(1, (int)Math.Ceiling(browser.ActualWidth));
            var height = Math.Max(1, (int)Math.Ceiling(browser.ActualHeight));
            var cursor = _cursorStates.GetOrAdd(windowId, _ => new RemoteCursorState());
            cursor.UpdateFromClient(x, y, width, height);
            switch (normalizedCommand)
            {
                case "move":
                    host.SendMouseMoveEvent(cursor.ToMouseEvent(), false);
                    try
                    {
                        await HighlightElementAtPointAsync(frame, cursor.X, cursor.Y).ConfigureAwait(true);
                    }
                    catch (TaskCanceledException)
                    {
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    return RemoteCommandResult.Success();
                case "click":
                case "ok":
                case "select":
                    host.SendMouseMoveEvent(cursor.ToMouseEvent(), false);
                    if (await IsMediaControlAtPointAsync(frame, cursor.X, cursor.Y).ConfigureAwait(true))
                    {
                        await CaptureDirectVideoOverlayFromPointAsync(windowId, frame, cursor.X, cursor.Y).ConfigureAwait(true);
                        AppLog.Write("BrowserControl", string.Format("click(media) => janela={0}, x={1}, y={2}", windowId.ToString("N"), cursor.X, cursor.Y));
                        host.SendMouseClickEvent(cursor.ToMouseEvent(), MouseButtonType.Left, false, 1);
                        host.SendMouseClickEvent(cursor.ToMouseEvent(), MouseButtonType.Left, true, 1);
                        await Task.Delay(120).ConfigureAwait(true);
                        InvalidateCapture(windowId);
                        return RemoteCommandResult.Success();
                    }

                    var clickResult = await ClickElementAtPointAsync(frame, cursor.X, cursor.Y).ConfigureAwait(true);
                    AppLog.Write("BrowserControl", string.Format("click(dom) => janela={0}, x={1}, y={2}, ok={3}, nav={4}", windowId.ToString("N"), cursor.X, cursor.Y, clickResult.Ok, clickResult.NavigationUrl ?? string.Empty));
                    if (!string.IsNullOrWhiteSpace(clickResult.NavigationUrl))
                    {
                        browser.Load(clickResult.NavigationUrl);
                    }

                    InvalidateCapture(windowId);
                    return clickResult;
                case "set-text":
                    var setTextResult = await SetTextAtFocusedElementAsync(frame, cursor.X, cursor.Y, text ?? string.Empty).ConfigureAwait(true);
                    InvalidateCapture(windowId);
                    return setTextResult;
                case "back":
                    SendKey(host, 0x1B);
                    InvalidateCapture(windowId);
                    return RemoteCommandResult.Success();
                case "reload":
                    browser.Reload();
                    InvalidateCapture(windowId);
                    return RemoteCommandResult.Success();
                case "history-back":
                    if (cefBrowser.CanGoBack)
                    {
                        cefBrowser.GoBack();
                        InvalidateCapture(windowId);
                        return RemoteCommandResult.Success();
                    }

                    return RemoteCommandResult.Success();
                case "history-forward":
                    if (cefBrowser.CanGoForward)
                    {
                        cefBrowser.GoForward();
                        InvalidateCapture(windowId);
                        return RemoteCommandResult.Success();
                    }

                    return RemoteCommandResult.Success();
                case "scroll-up":
                    var scrollUpResult = await ScrollPageAsync(frame, host, cursor.ToMouseEvent(), -420).ConfigureAwait(true);
                    InvalidateCapture(windowId);
                    return scrollUpResult;
                case "scroll-down":
                    var scrollDownResult = await ScrollPageAsync(frame, host, cursor.ToMouseEvent(), 420).ConfigureAwait(true);
                    InvalidateCapture(windowId);
                    return scrollDownResult;
                case "media-seek-backward":
                    var seekBackwardResult = await SeekMediaAsync(frame, host, -10).ConfigureAwait(true);
                    InvalidateCapture(windowId);
                    return seekBackwardResult;
                case "media-seek-forward":
                    var seekForwardResult = await SeekMediaAsync(frame, host, 10).ConfigureAwait(true);
                    InvalidateCapture(windowId);
                    return seekForwardResult;
                case "enter":
                    SendKey(host, 0x0D);
                    InvalidateCapture(windowId);
                    return RemoteCommandResult.Success();
                case "media-play-pause":
                    var mediaToggleResult = await ToggleMediaPlayPauseAsync(frame, host).ConfigureAwait(true);
                    AppLog.Write("BrowserControl", string.Format("media-play-pause => janela={0}, ok={1}", windowId.ToString("N"), mediaToggleResult.Ok));
                    InvalidateCapture(windowId);
                    return mediaToggleResult;
                case "media-play":
                    var mediaPlayResult = await PlayMediaAsync(frame, host).ConfigureAwait(true);
                    AppLog.Write("BrowserControl", string.Format("media-play => janela={0}, ok={1}", windowId.ToString("N"), mediaPlayResult.Ok));
                    InvalidateCapture(windowId);
                    return mediaPlayResult;
                case "media-resume":
                    var mediaResumeResult = await ResumeMediaWithNativeGestureAsync(frame, host).ConfigureAwait(true);
                    AppLog.Write("BrowserControl", string.Format("media-resume => janela={0}, ok={1}", windowId.ToString("N"), mediaResumeResult.Ok));
                    InvalidateCapture(windowId);
                    return mediaResumeResult;
                case "media-pause":
                    var mediaPauseResult = await PauseMediaAsync(frame, host).ConfigureAwait(true);
                    AppLog.Write("BrowserControl", string.Format("media-pause => janela={0}, ok={1}", windowId.ToString("N"), mediaPauseResult.Ok));
                    InvalidateCapture(windowId);
                    return mediaPauseResult;
                case "play":
                case "tab":
                    SendKey(host, 0x09);
                    InvalidateCapture(windowId);
                    return RemoteCommandResult.Success();
                case "open-fullscreen":
                    return RemoteCommandResult.Success();
                default:
                    AppLog.Write("RokuControl", string.Format("Comando desconhecido: {0}", normalizedCommand));
                    return RemoteCommandResult.Failure();
            }
        }).Task.Unwrap();
    }

    public async Task<bool> SendKeyInputAsync(Guid windowId, Key key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_browsers.TryGetValue(windowId, out var browser))
        {
            return false;
        }

        return await browser.Dispatcher.InvokeAsync(() =>
        {
            var cefBrowser = browser.GetBrowser();
            var host = cefBrowser?.GetHost();
            if (host is null)
            {
                return false;
            }

            host.SetFocus(true);
            host.SendFocusEvent(true);

            var keyCode = KeyInterop.VirtualKeyFromKey(key);
            if (keyCode <= 0)
            {
                return false;
            }

            host.SendKeyEvent(new KeyEvent
            {
                Type = KeyEventType.RawKeyDown,
                WindowsKeyCode = keyCode,
                NativeKeyCode = keyCode,
                FocusOnEditableField = true
            });

            host.SendKeyEvent(new KeyEvent
            {
                Type = KeyEventType.KeyUp,
                WindowsKeyCode = keyCode,
                NativeKeyCode = keyCode,
                FocusOnEditableField = true
            });

            InvalidateCapture(windowId);
            AppLog.Write("SuperPreviewControl", string.Format("Tecla local enviada ao CEF: key={0}, code={1}, janela={2}", key, keyCode, windowId.ToString("N")));
            return true;
        });
    }

    public async Task<bool> SendTextInputAsync(Guid windowId, string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(text) || !_browsers.TryGetValue(windowId, out var browser))
        {
            return false;
        }

        return await browser.Dispatcher.InvokeAsync(() =>
        {
            var cefBrowser = browser.GetBrowser();
            var host = cefBrowser?.GetHost();
            if (host is null)
            {
                return false;
            }

            host.SetFocus(true);
            host.SendFocusEvent(true);

            foreach (var ch in text)
            {
                host.SendKeyEvent(new KeyEvent
                {
                    Type = KeyEventType.Char,
                    WindowsKeyCode = ch,
                    NativeKeyCode = ch,
                    FocusOnEditableField = true
                });
            }

            InvalidateCapture(windowId);
            AppLog.Write("SuperPreviewControl", string.Format("Texto local enviado ao CEF: length={0}, janela={1}", text.Length, windowId.ToString("N")));
            return true;
        });
    }

    private static async Task<CachedBitmapFrame?> CaptureFreshBitmapFrameAsync(Guid windowId, ChromiumWebBrowser browser)
    {
        try
        {
            return await browser.Dispatcher.InvokeAsync<CachedBitmapFrame?>(() =>
            {
                var width = Math.Max(1, (int)Math.Ceiling(browser.ActualWidth));
                var height = Math.Max(1, (int)Math.Ceiling(browser.ActualHeight));

                if (width <= 1 || height <= 1)
                {
                    return null;
                }

                browser.Measure(new Size(width, height));
                browser.Arrange(new Rect(0, 0, width, height));
                browser.UpdateLayout();

                var renderTarget = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                renderTarget.Render(browser);
                var stride = width * 4;
                var pixels = new byte[stride * height];
                renderTarget.CopyPixels(pixels, stride, 0);
                return new CachedBitmapFrame(pixels, width, height, stride, DateTime.UtcNow);
            });
        }
        catch
        {
            return null;
        }
    }

    private void StartCaptureLoop(Guid windowId, ChromiumWebBrowser browser)
    {
        if (_captureLoops.TryRemove(windowId, out var existingCancellation))
        {
            try
            {
                existingCancellation.Cancel();
            }
            catch
            {
            }

            existingCancellation.Dispose();
        }

        var cancellation = new CancellationTokenSource();
        _captureLoops[windowId] = cancellation;
        _ = Task.Run(() => RunCaptureLoopAsync(windowId, browser, cancellation.Token), cancellation.Token);
    }

    private async Task RunCaptureLoopAsync(Guid windowId, ChromiumWebBrowser browser, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!_browsers.TryGetValue(windowId, out var currentBrowser) || !ReferenceEquals(currentBrowser, browser))
                {
                    break;
                }

                if (!_bitmapCaptureTasks.ContainsKey(windowId))
                {
                    var bitmapFrame = await CaptureFreshBitmapFrameAsync(windowId, browser).ConfigureAwait(false);
                    if (bitmapFrame is not null && bitmapFrame.Pixels.Length > 0)
                    {
                        _cachedBitmapFrames[windowId] = bitmapFrame;
                    }
                }

                await RefreshDirectVideoOverlayStateAsync(windowId, browser, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }

            try
            {
                await Task.Delay(BackgroundCaptureInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RefreshDirectVideoOverlayStateAsync(Guid windowId, ChromiumWebBrowser browser, CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        if (_lastDirectVideoOverlayProbeUtc.TryGetValue(windowId, out var lastProbeUtc) &&
            nowUtc - lastProbeUtc < DirectVideoOverlayProbeInterval)
        {
            return;
        }

        _lastDirectVideoOverlayProbeUtc[windowId] = nowUtc;
        var suppress = _directVideoSuppressionRequested.ContainsKey(windowId);
        var state = await EvaluateDirectVideoOverlayStateAsync(browser, suppress, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            _directVideoOverlayStates.TryRemove(windowId, out _);
            MaybeLogDirectVideoOverlay(
                windowId,
                new BrowserDirectVideoOverlayState
                {
                    Detected = false,
                    PageUrl = browser.Address ?? string.Empty,
                    DetectionReason = "probe-returned-null"
                });
            return;
        }

        if (!state.Detected)
        {
            _directVideoOverlayStates.TryRemove(windowId, out _);
            MaybeLogDirectVideoOverlay(windowId, state);
            return;
        }

        state.UpdatedUtc = nowUtc;
        _directVideoOverlayStates[windowId] = state;
        MaybeLogDirectVideoOverlay(windowId, state);
    }

    private async Task CaptureDirectVideoOverlayFromPointAsync(Guid windowId, IFrame frame, int x, int y)
    {
        try
        {
            var suppress = _directVideoSuppressionRequested.ContainsKey(windowId);
            var state = await EvaluateDirectVideoOverlayStateAtPointAsync(frame, x, y, suppress).ConfigureAwait(false);
            if (state is null || !state.Detected || string.IsNullOrWhiteSpace(state.SourceUrl))
            {
                return;
            }

            state.UpdatedUtc = DateTime.UtcNow;
            _directVideoOverlayStates[windowId] = state;
            _lastDirectVideoOverlayProbeUtc[windowId] = state.UpdatedUtc;
            MaybeLogDirectVideoOverlay(windowId, state);
        }
        catch
        {
        }
    }

    private void MaybeLogDirectVideoOverlay(Guid windowId, BrowserDirectVideoOverlayState state)
    {
        var message = !state.Detected
            ? string.Format(
                "Detector sem player YouTube => janela={0}, url={1}, host={2}, title={3}, reason={4}",
                windowId.ToString("N"),
                state.PageUrl,
                state.PageHost,
                state.PageTitle,
                state.DetectionReason)
            : string.Format(
                "Detector YouTube => janela={0}, source={1}, ready={2}, blocked={3}, embedded={4}, title={5}, reason={6}, rect={7:0.000},{8:0.000},{9:0.000},{10:0.000}",
                windowId.ToString("N"),
                state.SourceUrl,
                state.Ready,
                state.Blocked,
                state.Embedded,
                state.PageTitle,
                state.DetectionReason,
                state.NormalizedLeft,
                state.NormalizedTop,
                state.NormalizedWidth,
                state.NormalizedHeight);
        if (_lastDirectVideoOverlayLog.TryGetValue(windowId, out var previous) &&
            string.Equals(previous, message, StringComparison.Ordinal))
        {
            return;
        }

        _lastDirectVideoOverlayLog[windowId] = message;
        AppLog.Write("DirectOverlay", message);
    }

    private static async Task<BrowserDirectVideoOverlayState?> EvaluateDirectVideoOverlayStateAsync(ChromiumWebBrowser browser, bool suppress, CancellationToken cancellationToken)
    {
        return await browser.Dispatcher.InvokeAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cefBrowser = browser.GetBrowser();
            var frame = cefBrowser?.MainFrame;
            if (frame is null)
            {
                return null;
            }

            var suppressLiteral = suppress ? "true" : "false";
            var currentAddressLiteral = EscapeJavaScriptString(browser.Address ?? string.Empty);
            var script = string.Format(@"
(function() {{
  try {{
    const suppress = {0};
    const currentAddress = '{1}';
    const restoreHiddenNodes = () => {{
      const hiddenNodes = document.querySelectorAll('[data-super-direct-overlay-hidden=""1""]');
      hiddenNodes.forEach(node => {{
        node.style.visibility = '';
        node.style.pointerEvents = '';
        node.removeAttribute('data-super-direct-overlay-hidden');
      }});
    }};
    const normalizeYoutubeUrl = (rawUrl) => {{
      if (!rawUrl) return '';
      try {{
        const parsed = new URL(rawUrl, window.location.href);
        const host = (parsed.hostname || '').toLowerCase();
        if (host.indexOf('youtu.be') >= 0) {{
          const videoId = parsed.pathname.replace(/^\/+/, '').split('/')[0];
          return videoId ? `https://www.youtube.com/watch?v=${{videoId}}` : rawUrl;
        }}

        if (host.indexOf('youtube.com') >= 0 || host.indexOf('youtube-nocookie.com') >= 0) {{
          if (parsed.pathname.indexOf('/embed/') >= 0) {{
            const parts = parsed.pathname.split('/');
            const embedIndex = parts.indexOf('embed');
            const videoId = embedIndex >= 0 && embedIndex + 1 < parts.length ? parts[embedIndex + 1] : '';
            return videoId ? `https://www.youtube.com/watch?v=${{videoId}}` : rawUrl;
          }}

          if (parsed.pathname === '/watch' && parsed.searchParams.get('v')) {{
            return `https://www.youtube.com/watch?v=${{parsed.searchParams.get('v')}}`;
          }}
        }}
      }} catch (e) {{
      }}

      return rawUrl;
    }};
    const encodeResult = (result) => JSON.stringify(result);
    const viewportWidth = Math.max(window.innerWidth || 0, document.documentElement ? document.documentElement.clientWidth || 0 : 0, 1);
    const viewportHeight = Math.max(window.innerHeight || 0, document.documentElement ? document.documentElement.clientHeight || 0 : 0, 1);

    restoreHiddenNodes();

    const buildRect = (rect) => {{
      const rawLeft = rect.left || 0;
      const rawTop = rect.top || 0;
      const rawRight = rawLeft + (rect.width || 0);
      const rawBottom = rawTop + (rect.height || 0);
      const left = Math.max(0, Math.min(viewportWidth, rawLeft));
      const top = Math.max(0, Math.min(viewportHeight, rawTop));
      const right = Math.max(0, Math.min(viewportWidth, rawRight));
      const bottom = Math.max(0, Math.min(viewportHeight, rawBottom));
      const width = Math.max(0, right - left);
      const height = Math.max(0, bottom - top);
      return {{
        left,
        top,
        width,
        height,
        normalizedLeft: left / viewportWidth,
        normalizedTop: top / viewportHeight,
        normalizedWidth: width / viewportWidth,
        normalizedHeight: height / viewportHeight
      }};
    }};
    const pageUrl = window.location.href || currentAddress || '';
    const host = ((window.location && window.location.hostname) || '').toLowerCase();
    const pageTitle = ((document && document.title) || '').trim();
    const isYoutubeHost = host.indexOf('youtube.com') >= 0 || host.indexOf('youtu.be') >= 0 || host.indexOf('youtube-nocookie.com') >= 0;
    const youtubePlayer = document.querySelector('.html5-video-player, #movie_player, ytd-player, #player');
    const canonical = document.querySelector('link[rel=""canonical""]');
    const pageYoutubeUrl = normalizeYoutubeUrl(canonical && canonical.href ? canonical.href : pageUrl);

    if ((isYoutubeHost || pageUrl.toLowerCase().indexOf('youtube') >= 0) && youtubePlayer) {{
      const rect = youtubePlayer.getBoundingClientRect();
      if (rect.width >= 120 && rect.height >= 90) {{
        const activeVideo = document.querySelector('video.html5-main-video, video.video-stream, video');
        let blocked = false;
        if (suppress && activeVideo) {{
          try {{
            activeVideo.muted = true;
            if (typeof activeVideo.pause === 'function') {{
              activeVideo.pause();
            }}
            blocked = true;
          }} catch (e) {{
          }}
        }}

        return encodeResult(Object.assign({{
          detected: true,
          ready: true,
          provider: 'youtube',
          sourceUrl: pageYoutubeUrl,
          pageUrl: pageUrl,
          pageHost: host,
          pageTitle: pageTitle,
          detectionReason: 'page-player',
          blocked: blocked,
          embedded: false
        }}, buildRect(rect)));
      }}
    }}

    const activeVideo = document.querySelector('video.html5-main-video, video.video-stream, video');
    if (isYoutubeHost && activeVideo) {{
      const rectSource = activeVideo.closest('.html5-video-player') || activeVideo;
      const rect = rectSource.getBoundingClientRect();
      if (rect.width >= 120 && rect.height >= 90) {{
        const sourceUrl = pageYoutubeUrl;
        let blocked = false;
        if (suppress) {{
          try {{
            activeVideo.muted = true;
            if (typeof activeVideo.pause === 'function') {{
              activeVideo.pause();
            }}
            blocked = true;
          }} catch (e) {{
          }}
        }}

        return encodeResult(Object.assign({{
          detected: true,
          ready: !!(activeVideo.readyState >= 2 || activeVideo.currentSrc || activeVideo.src),
          provider: 'youtube',
          sourceUrl: sourceUrl,
          pageUrl: pageUrl,
          pageHost: host,
          pageTitle: pageTitle,
          detectionReason: 'page-video',
          blocked: blocked,
          embedded: false
        }}, buildRect(rect)));
      }}
    }}

    const iframe = Array.from(document.querySelectorAll('iframe')).find(node => {{
      const src = ((node.getAttribute && node.getAttribute('src')) || node.src || '').toLowerCase();
      return src.indexOf('youtube.com/embed/') >= 0 ||
             src.indexOf('youtube-nocookie.com/embed/') >= 0 ||
             src.indexOf('youtube.com/watch') >= 0 ||
             src.indexOf('youtu.be/') >= 0;
    }});
    if (iframe) {{
      const rect = iframe.getBoundingClientRect();
      if (rect.width >= 120 && rect.height >= 90) {{
        if (suppress) {{
          iframe.style.visibility = 'hidden';
          iframe.style.pointerEvents = 'none';
          iframe.setAttribute('data-super-direct-overlay-hidden', '1');
        }}

        return encodeResult(Object.assign({{
          detected: true,
          ready: true,
          provider: 'youtube',
          sourceUrl: normalizeYoutubeUrl((iframe.getAttribute && iframe.getAttribute('src')) || iframe.src || ''),
          pageUrl: pageUrl,
          pageHost: host,
          pageTitle: pageTitle,
          detectionReason: 'page-iframe',
          blocked: suppress,
          embedded: true
        }}, buildRect(rect)));
      }}
    }}

    return encodeResult({{
      detected: false,
      ready: false,
      provider: '',
      sourceUrl: '',
      pageUrl: pageUrl,
      pageHost: host,
      pageTitle: pageTitle,
      detectionReason: youtubePlayer ? 'player-too-small' : (isYoutubeHost ? 'youtube-page-no-player' : 'not-youtube-page'),
      left: 0,
      top: 0,
      width: 0,
      height: 0,
      normalizedLeft: 0,
      normalizedTop: 0,
      normalizedWidth: 0,
      normalizedHeight: 0,
      blocked: false,
      embedded: false
    }});
  }} catch (error) {{
    return JSON.stringify({{
      detected: false,
      ready: false,
      provider: '',
      sourceUrl: '',
      pageUrl: currentAddress || '',
      pageHost: '',
      pageTitle: '',
      detectionReason: 'script-error',
      left: 0,
      top: 0,
      width: 0,
      height: 0,
      normalizedLeft: 0,
      normalizedTop: 0,
      normalizedWidth: 0,
      normalizedHeight: 0,
      blocked: false,
      embedded: false
    }});
  }}
}})();", suppressLiteral, currentAddressLiteral);

            var response = await frame.EvaluateScriptAsync(script).ConfigureAwait(true);
            return ParseJsonResult<BrowserDirectVideoOverlayState>(response?.Result as string);
        }).Task.Unwrap().ConfigureAwait(false);
    }

    private static async Task<BrowserDirectVideoOverlayState?> EvaluateDirectVideoOverlayStateAtPointAsync(IFrame frame, int x, int y, bool suppress)
    {
        var suppressLiteral = suppress ? "true" : "false";
        var script = string.Format(@"
(function() {{
  try {{
    const x = {0};
    const y = {1};
    const suppress = {2};
    const viewportWidth = Math.max(window.innerWidth || 0, document.documentElement ? document.documentElement.clientWidth || 0 : 0, 1);
    const viewportHeight = Math.max(window.innerHeight || 0, document.documentElement ? document.documentElement.clientHeight || 0 : 0, 1);
    const normalizeYoutubeUrl = (rawUrl) => {{
      if (!rawUrl) return '';
      try {{
        const parsed = new URL(rawUrl, window.location.href);
        const host = (parsed.hostname || '').toLowerCase();
        if (host.indexOf('youtu.be') >= 0) {{
          const videoId = parsed.pathname.replace(/^\/+/, '').split('/')[0];
          return videoId ? `https://www.youtube.com/watch?v=${{videoId}}` : rawUrl;
        }}
        if (host.indexOf('youtube.com') >= 0 || host.indexOf('youtube-nocookie.com') >= 0) {{
          if (parsed.pathname.indexOf('/embed/') >= 0) {{
            const parts = parsed.pathname.split('/');
            const embedIndex = parts.indexOf('embed');
            const videoId = embedIndex >= 0 && embedIndex + 1 < parts.length ? parts[embedIndex + 1] : '';
            return videoId ? `https://www.youtube.com/watch?v=${{videoId}}` : rawUrl;
          }}
          if (parsed.pathname === '/watch' && parsed.searchParams.get('v')) {{
            return `https://www.youtube.com/watch?v=${{parsed.searchParams.get('v')}}`;
          }}
        }}
      }} catch (e) {{
      }}
      return rawUrl;
    }};
    const buildRect = (rect) => {{
      const rawLeft = rect.left || 0;
      const rawTop = rect.top || 0;
      const rawRight = rawLeft + (rect.width || 0);
      const rawBottom = rawTop + (rect.height || 0);
      const left = Math.max(0, Math.min(viewportWidth, rawLeft));
      const top = Math.max(0, Math.min(viewportHeight, rawTop));
      const right = Math.max(0, Math.min(viewportWidth, rawRight));
      const bottom = Math.max(0, Math.min(viewportHeight, rawBottom));
      const width = Math.max(0, right - left);
      const height = Math.max(0, bottom - top);
      return {{
        left,
        top,
        width,
        height,
        normalizedLeft: left / viewportWidth,
        normalizedTop: top / viewportHeight,
        normalizedWidth: width / viewportWidth,
        normalizedHeight: height / viewportHeight
      }};
    }};

    const target = document.elementFromPoint(x, y);
    if (!target) return JSON.stringify({{ detected: false }});

    const player = target.closest('.html5-video-player, ytd-player, #movie_player');
    const video = target.closest('video') || document.querySelector('video.html5-main-video, video.video-stream, video');
    const iframe = target.closest('iframe') || target;
    const iframeSrc = iframe && ((iframe.getAttribute && iframe.getAttribute('src')) || iframe.src || '');

    if (player) {{
      const rect = player.getBoundingClientRect();
      const sourceUrl = normalizeYoutubeUrl(window.location.href || '');
      if (suppress && video && typeof video.pause === 'function') {{
        try {{
          video.pause();
          video.muted = true;
        }} catch (e) {{
        }}
      }}
      return JSON.stringify(Object.assign({{
        detected: rect.width >= 120 && rect.height >= 90,
        ready: true,
        provider: 'youtube',
        sourceUrl: sourceUrl,
        pageUrl: window.location.href || '',
        pageHost: ((window.location && window.location.hostname) || '').toLowerCase(),
        pageTitle: ((document && document.title) || '').trim(),
        detectionReason: 'point-player',
        blocked: !!suppress,
        embedded: false
      }}, buildRect(rect)));
    }}

    if (iframeSrc && (iframeSrc.toLowerCase().indexOf('youtube.com') >= 0 || iframeSrc.toLowerCase().indexOf('youtu.be') >= 0)) {{
      const rect = iframe.getBoundingClientRect();
      if (suppress) {{
        iframe.style.visibility = 'hidden';
        iframe.style.pointerEvents = 'none';
        iframe.setAttribute('data-super-direct-overlay-hidden', '1');
      }}
      return JSON.stringify(Object.assign({{
        detected: rect.width >= 120 && rect.height >= 90,
        ready: true,
        provider: 'youtube',
        sourceUrl: normalizeYoutubeUrl(iframeSrc),
        pageUrl: window.location.href || '',
        pageHost: ((window.location && window.location.hostname) || '').toLowerCase(),
        pageTitle: ((document && document.title) || '').trim(),
        detectionReason: 'point-iframe',
        blocked: !!suppress,
        embedded: true
      }}, buildRect(rect)));
    }}

    if (video) {{
      const rectSource = video.closest('.html5-video-player') || video;
      const rect = rectSource.getBoundingClientRect();
      const sourceUrl = normalizeYoutubeUrl(window.location.href || '');
      if (suppress) {{
        try {{
          video.pause();
          video.muted = true;
        }} catch (e) {{
        }}
      }}
      return JSON.stringify(Object.assign({{
        detected: rect.width >= 120 && rect.height >= 90,
        ready: true,
        provider: sourceUrl.toLowerCase().indexOf('youtube') >= 0 ? 'youtube' : 'html5',
        sourceUrl: sourceUrl,
        pageUrl: window.location.href || '',
        pageHost: ((window.location && window.location.hostname) || '').toLowerCase(),
        pageTitle: ((document && document.title) || '').trim(),
        detectionReason: 'point-video',
        blocked: !!suppress,
        embedded: false
      }}, buildRect(rect)));
    }}

    return JSON.stringify({{
      detected: false,
      pageUrl: window.location.href || '',
      pageHost: ((window.location && window.location.hostname) || '').toLowerCase(),
      pageTitle: ((document && document.title) || '').trim(),
      detectionReason: 'point-no-player'
    }});
  }} catch (e) {{
    return JSON.stringify({{ detected: false, detectionReason: 'point-script-error' }});
  }}
}})();", x, y, suppressLiteral);

        var response = await frame.EvaluateScriptAsync(script).ConfigureAwait(false);
        return ParseJsonResult<BrowserDirectVideoOverlayState>(response?.Result as string);
    }

    private Task RestoreDirectVideoPresentationAsync(Guid windowId)
    {
        if (!_browsers.TryGetValue(windowId, out var browser))
        {
            return Task.CompletedTask;
        }

        return browser.Dispatcher.InvokeAsync(async () =>
        {
            var cefBrowser = browser.GetBrowser();
            var frame = cefBrowser?.MainFrame;
            if (frame is null)
            {
                return;
            }

            const string restoreScript = @"
(function() {
  try {
    document.querySelectorAll('[data-super-direct-overlay-hidden=""1""]').forEach(function(node) {
      node.style.visibility = '';
      node.style.pointerEvents = '';
      node.removeAttribute('data-super-direct-overlay-hidden');
    });
  } catch (e) {
  }
})();";

            await frame.EvaluateScriptAsync(restoreScript).ConfigureAwait(true);
        }).Task.Unwrap();
    }

    private static Task<byte[]?> EncodeJpegAsync(Guid windowId, CachedBitmapFrame frame)
    {
        return Task.Run(() =>
        {
            try
            {
                using var stream = new MemoryStream();
                var bitmap = BitmapSource.Create(
                    frame.Width,
                    frame.Height,
                    96,
                    96,
                    PixelFormats.Pbgra32,
                    null,
                    frame.Pixels,
                    frame.Stride);
                var encoder = new JpegBitmapEncoder
                {
                    QualityLevel = 68
                };
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(stream);
                return (byte[]?)stream.ToArray();
            }
            catch
            {
                return null;
            }
        });
    }

    private static void SendKey(IBrowserHost host, int keyCode)
    {
        host.SendKeyEvent(new KeyEvent
        {
            Type = KeyEventType.RawKeyDown,
            WindowsKeyCode = keyCode,
            NativeKeyCode = keyCode,
            FocusOnEditableField = true
        });

        host.SendKeyEvent(new KeyEvent
        {
            Type = KeyEventType.KeyUp,
            WindowsKeyCode = keyCode,
            NativeKeyCode = keyCode,
            FocusOnEditableField = true
        });
    }

    private static void SendCharacterKey(IBrowserHost host, char character, int keyCode)
    {
        host.SendKeyEvent(new KeyEvent
        {
            Type = KeyEventType.RawKeyDown,
            WindowsKeyCode = keyCode,
            NativeKeyCode = keyCode,
            FocusOnEditableField = true
        });

        host.SendKeyEvent(new KeyEvent
        {
            Type = KeyEventType.Char,
            WindowsKeyCode = character,
            NativeKeyCode = character,
            FocusOnEditableField = true
        });

        host.SendKeyEvent(new KeyEvent
        {
            Type = KeyEventType.KeyUp,
            WindowsKeyCode = keyCode,
            NativeKeyCode = keyCode,
            FocusOnEditableField = true
        });
    }

    private static async Task HighlightElementAtPointAsync(IFrame frame, int x, int y)
    {
        try
        {
            var script = string.Format(@"
(function() {{
  const x = {0};
  const y = {1};
  const previous = document.querySelectorAll('[data-roku-hover]');
  previous.forEach(el => {{
    el.style.outline = '';
    el.style.outlineOffset = '';
    el.removeAttribute('data-roku-hover');
  }});

  const target = document.elementFromPoint(x, y);
  if (!target) return false;

  target.setAttribute('data-roku-hover', '1');
  target.style.outline = '4px solid #38bdf8';
  target.style.outlineOffset = '2px';
  return true;
}})();", x, y);

            await frame.EvaluateScriptAsync(script).ConfigureAwait(false);
            await Task.Delay(60).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private static async Task<RemoteCommandResult> ClickElementAtPointAsync(IFrame frame, int x, int y)
    {
        var script = string.Format(@"
(function() {{
  const clearEditMarkers = () => document.querySelectorAll('[data-roku-edit-target]').forEach(el => el.removeAttribute('data-roku-edit-target'));
  const describeNode = (el) => {{
    if (!el) return '';
    const tag = (el.tagName || '').toUpperCase();
    const id = el.id ? `#${{el.id}}` : '';
    const cls = typeof el.className === 'string' && el.className.trim() !== ''
      ? '.' + el.className.trim().split(/\s+/).slice(0, 3).join('.')
      : '';
    return `${{tag}}${{id}}${{cls}}`;
  }};
  const findClickableTarget = (start) => {{
    let node = start;
    while (node) {{
      const tag = (node.tagName || '').toUpperCase();
      const role = ((node.getAttribute && node.getAttribute('role')) || '').toLowerCase();
      const href = (node.getAttribute && node.getAttribute('href')) || '';
      const tabIndex = node.tabIndex;
      const hasOnClick = !!node.onclick;
      const className = typeof node.className === 'string' ? node.className : '';
      const isYoutubeEndpoint = className.indexOf('yt-simple-endpoint') >= 0;
      const isYoutubeThumb = tag === 'YTD-THUMBNAIL' || className.indexOf('ytd-thumbnail') >= 0;
      const isSemanticClickable =
        tag === 'A' ||
        tag === 'BUTTON' ||
        tag === 'SUMMARY' ||
        role === 'button' ||
        href !== '' ||
        hasOnClick ||
        isYoutubeEndpoint ||
        isYoutubeThumb;

      if (isSemanticClickable || (tabIndex >= 0 && tag !== 'IMG' && tag !== 'DIV' && tag !== 'SPAN')) {{
        if (typeof node.click === 'function') {{
          return node;
        }}
      }}

      node = node.parentElement;
    }}
    return null;
  }};
  const dispatchClickSequence = (el) => {{
    const events = ['pointerdown', 'mousedown', 'pointerup', 'mouseup', 'click'];
    for (const name of events) {{
      el.dispatchEvent(new MouseEvent(name, {{ bubbles: true, cancelable: true, view: window }}));
    }}
  }};

  const x = {0};
  const y = {1};
  const target = document.elementFromPoint(x, y);
  if (!target) return JSON.stringify({{ ok: false }});

  if (typeof target.focus === 'function') target.focus();
  clearEditMarkers();

  const tag = (target.tagName || '').toUpperCase();
  const inputType = ((target.type || '') + '').toLowerCase();
  const isTextInput =
    tag === 'TEXTAREA' ||
    (tag === 'INPUT' && ['button', 'submit', 'reset', 'checkbox', 'radio', 'range', 'color', 'file', 'hidden', 'image'].indexOf(inputType) === -1);
  const editableTarget = isTextInput || !!target.isContentEditable
    ? target
    : target.closest('input, textarea, select, [contenteditable=""true""]');
  const isEditable = !!editableTarget;
  const clickable = findClickableTarget(target);
  const mediaControl = target.closest('video, .html5-video-player, .ytp-chrome-controls, .ytp-play-button, .ytp-progress-bar');
  let currentValue = '';

  if (isEditable) {{
    const effectiveTarget = editableTarget;
    const effectiveTag = (effectiveTarget.tagName || '').toUpperCase();
    const effectiveType = ((effectiveTarget.type || '') + '').toLowerCase();
    currentValue = effectiveType === 'password'
      ? ''
      : (typeof effectiveTarget.value === 'string' ? effectiveTarget.value : (typeof effectiveTarget.innerText === 'string' ? effectiveTarget.innerText : ''));
    effectiveTarget.setAttribute('data-roku-edit-target', '1');
    effectiveTarget.style.outline = '4px solid #38bdf8';
    effectiveTarget.style.outlineOffset = '2px';
    if (typeof effectiveTarget.focus === 'function') effectiveTarget.focus();
    if (typeof effectiveTarget.select === 'function' && (effectiveTag === 'INPUT' || effectiveTag === 'TEXTAREA')) {{
      effectiveTarget.select();
    }}
    return JSON.stringify({{
      ok: true,
      editable: true,
      multiline: effectiveTag === 'TEXTAREA',
      tagName: effectiveTag,
      inputType: effectiveType,
      value: currentValue
    }});
  }}

  if (mediaControl) {{
    const activeMedia = target.closest('video, audio') || document.querySelector('video, audio');
    if (activeMedia && typeof activeMedia.paused === 'boolean') {{
      if (activeMedia.paused) {{
        const playResult = activeMedia.play && activeMedia.play();
        if (playResult && typeof playResult.catch === 'function') {{
          playResult.catch(function(){{}});
        }}
      }}

      return JSON.stringify({{
        ok: true,
        editable: false,
        multiline: false,
        tagName: tag,
        inputType: inputType,
        value: '',
        targetPath: describeNode(target),
        clickablePath: describeNode(activeMedia),
        navigationUrl: ''
      }});
    }}

    const youtubeButton = target.closest('.ytp-play-button') || document.querySelector('.ytp-play-button');
    if (youtubeButton && typeof youtubeButton.click === 'function') {{
      const ariaLabel = ((youtubeButton.getAttribute && youtubeButton.getAttribute('aria-label')) || '').toLowerCase();
      const title = ((youtubeButton.getAttribute && youtubeButton.getAttribute('title')) || '').toLowerCase();
      const shouldPlay = ariaLabel.indexOf('play') >= 0 || title.indexOf('play') >= 0 || ariaLabel.indexOf('reprodu') >= 0 || title.indexOf('reprodu') >= 0;
      if (shouldPlay) {{
        if (typeof youtubeButton.focus === 'function') youtubeButton.focus();
        youtubeButton.click();
      }}
      return JSON.stringify({{
        ok: true,
        editable: false,
        multiline: false,
        tagName: tag,
        inputType: inputType,
        value: '',
        targetPath: describeNode(target),
        clickablePath: describeNode(youtubeButton),
        navigationUrl: ''
      }});
    }}
  }}

  if (clickable && !mediaControl && typeof clickable.click === 'function') {{
    const clickableTag = (clickable.tagName || '').toUpperCase();
    const clickableHref = (clickable.getAttribute && clickable.getAttribute('href')) || '';
    if (typeof clickable.focus === 'function') clickable.focus();
    dispatchClickSequence(clickable);
    clickable.click();
    const navUrl = clickableTag === 'A' && clickableHref ? (clickable.href || clickableHref) : '';
    return JSON.stringify({{
      ok: true,
      editable: false,
      multiline: false,
      tagName: tag,
      inputType: inputType,
      value: '',
      targetPath: describeNode(target),
      clickablePath: describeNode(clickable),
      navigationUrl: navUrl
    }});
  }}

  return JSON.stringify({{
    ok: true,
    editable: false,
    multiline: false,
    tagName: tag,
    inputType: inputType,
    value: '',
    targetPath: describeNode(target),
    clickablePath: describeNode(clickable),
    navigationUrl: ''
  }});
}})();", x, y);

        var response = await frame.EvaluateScriptAsync(script).ConfigureAwait(false);
        await Task.Delay(120).ConfigureAwait(false);
        return ParseRemoteCommandResult(response?.Result as string);
    }

    private static async Task<bool> IsMediaControlAtPointAsync(IFrame frame, int x, int y)
    {
        var script = string.Format(@"
(function() {{
  try {{
    const target = document.elementFromPoint({0}, {1});
    if (!target) return 'false';
    const mediaControl = target.closest('video, .html5-video-player, .ytp-chrome-controls, .ytp-play-button, .ytp-progress-bar');
    return mediaControl ? 'true' : 'false';
  }} catch (e) {{
    return 'false';
  }}
}})();", x, y);

        var response = await frame.EvaluateScriptAsync(script).ConfigureAwait(false);
        return string.Equals(response?.Result as string, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<RemoteCommandResult> SetTextAtFocusedElementAsync(IFrame frame, int x, int y, string text)
    {
        var escapedText = EscapeJavaScriptString(text);
        var script = string.Format(@"
(function() {{
  const findEditableTarget = () => {{
    const markedTarget = document.querySelector('[data-roku-edit-target=""1""]');
    const pointTarget = document.elementFromPoint({1}, {2});
    const activeTarget = document.activeElement;
    const candidates = [markedTarget, activeTarget, pointTarget];

    for (const candidate of candidates) {{
      if (!candidate) continue;
      const tag = (candidate.tagName || '').toUpperCase();
      const inputType = ((candidate.type || '') + '').toLowerCase();
      const isTextInput =
        tag === 'TEXTAREA' ||
        (tag === 'INPUT' && ['button', 'submit', 'reset', 'checkbox', 'radio', 'range', 'color', 'file', 'hidden', 'image'].indexOf(inputType) === -1);
      const isEditable = isTextInput || !!candidate.isContentEditable;
      if (isEditable) return candidate;
    }}

    return null;
  }};

  const target = findEditableTarget();
  if (!target) return JSON.stringify({{ ok: false, editable: false, value: '' }});

  const tag = (target.tagName || '').toUpperCase();
  const inputType = ((target.type || '') + '').toLowerCase();
  const isTextInput =
    tag === 'TEXTAREA' ||
    (tag === 'INPUT' && ['button', 'submit', 'reset', 'checkbox', 'radio', 'range', 'color', 'file', 'hidden', 'image'].indexOf(inputType) === -1);
  const isEditable = isTextInput || !!target.isContentEditable;
  if (!isEditable) return JSON.stringify({{ ok: false, editable: false, value: '' }});

  const value = '{0}';
  if (typeof target.focus === 'function') target.focus();

  if (typeof target.value === 'string') {{
    const lastValue = target.value;
    const proto = tag === 'TEXTAREA'
      ? window.HTMLTextAreaElement && window.HTMLTextAreaElement.prototype
      : window.HTMLInputElement && window.HTMLInputElement.prototype;
    const descriptor = proto ? Object.getOwnPropertyDescriptor(proto, 'value') : null;
    if (descriptor && typeof descriptor.set === 'function') {{
      descriptor.set.call(target, value);
    }} else {{
      target.value = value;
    }}
    if (target._valueTracker && typeof target._valueTracker.setValue === 'function') {{
      target._valueTracker.setValue(lastValue);
    }}
    if (typeof target.setSelectionRange === 'function') {{
      target.setSelectionRange(value.length, value.length);
    }}
  }} else if (target.isContentEditable) {{
    target.innerText = value;
  }}

  target.dispatchEvent(new KeyboardEvent('keydown', {{ bubbles: true, key: 'Process' }}));
  target.dispatchEvent(new InputEvent('beforeinput', {{ bubbles: true, cancelable: true, data: value, inputType: 'insertText' }}));
  target.dispatchEvent(new Event('input', {{ bubbles: true }}));
  target.dispatchEvent(new Event('change', {{ bubbles: true }}));
  target.dispatchEvent(new KeyboardEvent('keyup', {{ bubbles: true, key: 'Process' }}));

  return JSON.stringify({{
    ok: true,
    editable: true,
    multiline: tag === 'TEXTAREA',
    tagName: tag,
    inputType: inputType,
    value: (inputType === 'password') ? '' : (typeof target.value === 'string' ? target.value : value),
    navigationUrl: ''
  }});
}})();", escapedText, x, y);

        var response = await frame.EvaluateScriptAsync(script).ConfigureAwait(false);
        await Task.Delay(120).ConfigureAwait(false);
        return ParseRemoteCommandResult(response?.Result as string);
    }

    private static async Task<RemoteCommandResult> ToggleMediaPlayPauseAsync(IFrame frame, IBrowserHost host)
    {
        var script = @"
(function() {
  try {
    const activeMedia = Array.from(document.querySelectorAll('video, audio')).find(el => !!el && typeof el.paused === 'boolean');
    if (activeMedia) {
      if (activeMedia.paused) {
        const playResult = activeMedia.play();
        if (playResult && typeof playResult.catch === 'function') {
          playResult.catch(function(){});
        }
      } else {
        activeMedia.pause();
      }
      return JSON.stringify({ ok: true });
    }

    const youtubeButton = document.querySelector('.ytp-play-button');
    if (youtubeButton && typeof youtubeButton.click === 'function') {
      youtubeButton.click();
      return JSON.stringify({ ok: true });
    }

    return JSON.stringify({ ok: false });
  } catch (e) {
    return JSON.stringify({ ok: false });
  }
})();";

        var response = await frame.EvaluateScriptAsync(script).ConfigureAwait(false);
        var result = ParseRemoteCommandResult(response?.Result as string);
        if (!result.Ok)
        {
            SendKey(host, 0xB3);
            result = RemoteCommandResult.Success();
        }

        await Task.Delay(120).ConfigureAwait(false);
        return result;
    }

    private static async Task<RemoteCommandResult> PlayMediaAsync(IFrame frame, IBrowserHost host)
    {
        var script = @"
(function() {
  try {
    const activeMedia = Array.from(document.querySelectorAll('video, audio')).find(el => !!el && typeof el.paused === 'boolean');
    if (activeMedia) {
      if (activeMedia.paused) {
        const playResult = activeMedia.play();
        if (playResult && typeof playResult.catch === 'function') {
          playResult.catch(function(){});
        }
      }
      return JSON.stringify({ ok: true });
    }

    const youtubeButton = document.querySelector('.ytp-play-button');
    if (youtubeButton && typeof youtubeButton.click === 'function') {
      const ariaLabel = ((youtubeButton.getAttribute && youtubeButton.getAttribute('aria-label')) || '').toLowerCase();
      const title = ((youtubeButton.getAttribute && youtubeButton.getAttribute('title')) || '').toLowerCase();
      const shouldPlay = ariaLabel.indexOf('play') >= 0 || title.indexOf('play') >= 0 || ariaLabel.indexOf('reprodu') >= 0 || title.indexOf('reprodu') >= 0;
      if (shouldPlay) {
        youtubeButton.click();
      }
      return JSON.stringify({ ok: true });
    }

    return JSON.stringify({ ok: false });
  } catch (e) {
    return JSON.stringify({ ok: false });
  }
})();";

        var response = await frame.EvaluateScriptAsync(script).ConfigureAwait(false);
        var result = ParseRemoteCommandResult(response?.Result as string);
        if (!result.Ok)
        {
            SendKey(host, 0xB3);
            result = RemoteCommandResult.Success();
        }

        await Task.Delay(120).ConfigureAwait(false);
        return result;
    }

    private static async Task<RemoteCommandResult> PauseMediaAsync(IFrame frame, IBrowserHost host)
    {
        var script = @"
(function() {
  try {
    const activeMedia = Array.from(document.querySelectorAll('video, audio')).find(el => !!el && typeof el.paused === 'boolean');
    if (activeMedia) {
      if (!activeMedia.paused && typeof activeMedia.pause === 'function') {
        activeMedia.pause();
      }
      return JSON.stringify({ ok: true });
    }

    const youtubeButton = document.querySelector('.ytp-play-button');
    if (youtubeButton && typeof youtubeButton.click === 'function') {
      const ariaLabel = ((youtubeButton.getAttribute && youtubeButton.getAttribute('aria-label')) || '').toLowerCase();
      const title = ((youtubeButton.getAttribute && youtubeButton.getAttribute('title')) || '').toLowerCase();
      const shouldPause = ariaLabel.indexOf('pause') >= 0 || title.indexOf('pause') >= 0 || ariaLabel.indexOf('paus') >= 0 || title.indexOf('paus') >= 0;
      if (shouldPause) {
        youtubeButton.click();
      }
      return JSON.stringify({ ok: true });
    }

    return JSON.stringify({ ok: false });
  } catch (e) {
    return JSON.stringify({ ok: false });
  }
})();";

        var response = await frame.EvaluateScriptAsync(script).ConfigureAwait(false);
        var result = ParseRemoteCommandResult(response?.Result as string);
        if (!result.Ok)
        {
            SendKey(host, 0xB3);
            result = RemoteCommandResult.Success();
        }

        await Task.Delay(120).ConfigureAwait(false);
        return result;
    }

    private static async Task<RemoteCommandResult> ResumeMediaWithNativeGestureAsync(IFrame frame, IBrowserHost host)
    {
        var script = @"
(function() {
  try {
    const activeMedia = Array.from(document.querySelectorAll('video, audio')).find(el => !!el && typeof el.paused === 'boolean');
    if (activeMedia) {
      return JSON.stringify({ ok: true, paused: !!activeMedia.paused });
    }

    const youtubeButton = document.querySelector('.ytp-play-button');
    if (youtubeButton) {
      const ariaLabel = ((youtubeButton.getAttribute && youtubeButton.getAttribute('aria-label')) || '').toLowerCase();
      const title = ((youtubeButton.getAttribute && youtubeButton.getAttribute('title')) || '').toLowerCase();
      const paused = ariaLabel.indexOf('play') >= 0 || title.indexOf('play') >= 0 || ariaLabel.indexOf('reprodu') >= 0 || title.indexOf('reprodu') >= 0;
      return JSON.stringify({ ok: true, paused: paused });
    }

    return JSON.stringify({ ok: false, paused: false });
  } catch (e) {
    return JSON.stringify({ ok: false, paused: false });
  }
})();";

        var response = await frame.EvaluateScriptAsync(script).ConfigureAwait(false);
        var result = ParseRemoteCommandResult(response?.Result as string);
        var paused = false;
        try
        {
            var json = response?.Result as string;
            if (!string.IsNullOrWhiteSpace(json))
            {
                paused = json.IndexOf(@"""paused"":true", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        catch
        {
        }

        if (result.Ok && paused)
        {
            SendCharacterKey(host, 'k', 0x4B);
        }

        await Task.Delay(120).ConfigureAwait(false);
        return result.Ok ? RemoteCommandResult.Success() : result;
    }

    private static async Task<RemoteCommandResult> SeekMediaAsync(IFrame frame, IBrowserHost host, int secondsDelta)
    {
        var script = string.Format(@"
(function() {{
  try {{
    const media = Array.from(document.querySelectorAll('video, audio')).find(el => !!el && typeof el.currentTime === 'number');
    if (!media) {{
      return JSON.stringify({{ ok: false }});
    }}

    const targetTime = Math.max(0, (media.currentTime || 0) + ({0}));
    media.currentTime = targetTime;
    return JSON.stringify({{ ok: true }});
  }} catch (e) {{
    return JSON.stringify({{ ok: false }});
  }}
}})();", secondsDelta);

        var response = await frame.EvaluateScriptAsync(script).ConfigureAwait(false);
        var result = ParseRemoteCommandResult(response?.Result as string);
        if (!result.Ok)
        {
            SendKey(host, secondsDelta < 0 ? 0x25 : 0x27);
            result = RemoteCommandResult.Success();
        }

        await Task.Delay(120).ConfigureAwait(false);
        return result;
    }

    private static async Task<RemoteCommandResult> ScrollPageAsync(IFrame frame, IBrowserHost host, MouseEvent mouseEvent, int deltaY)
    {
        host.SendMouseMoveEvent(mouseEvent, false);
        host.SendMouseWheelEvent(mouseEvent, 0, deltaY < 0 ? 120 : -120);

        var script = string.Format(@"
(function() {{
  try {{
    const x = {1};
    const y = {2};
    const delta = {0};
    const target = document.elementFromPoint(x, y);
    const isScrollable = (el) => {{
      if (!el) return false;
      const style = window.getComputedStyle(el);
      const overflowY = style ? style.overflowY : '';
      return (overflowY === 'auto' || overflowY === 'scroll' || overflowY === 'overlay') && el.scrollHeight > el.clientHeight;
    }};

    let scrollTarget = target;
    while (scrollTarget && !isScrollable(scrollTarget)) {{
      scrollTarget = scrollTarget.parentElement;
    }}

    if (!scrollTarget) {{
      scrollTarget = document.scrollingElement || document.documentElement || document.body;
    }}

    if (!scrollTarget) {{
      return JSON.stringify({{ ok: false }});
    }}

    const before = scrollTarget === document.body || scrollTarget === document.documentElement || scrollTarget === document.scrollingElement
      ? (window.scrollY || scrollTarget.scrollTop || 0)
      : (scrollTarget.scrollTop || 0);

    if (scrollTarget === document.body || scrollTarget === document.documentElement || scrollTarget === document.scrollingElement) {{
      window.scrollBy({{ top: delta, left: 0, behavior: 'auto' }});
    }} else {{
      scrollTarget.scrollTop = (scrollTarget.scrollTop || 0) + delta;
    }}

    const after = scrollTarget === document.body || scrollTarget === document.documentElement || scrollTarget === document.scrollingElement
      ? (window.scrollY || scrollTarget.scrollTop || 0)
      : (scrollTarget.scrollTop || 0);
    return JSON.stringify({{ ok: true, before: before, after: after, moved: before <> after }});
  }} catch (e) {{
    return JSON.stringify({{ ok: false }});
  }}
}})();", deltaY, mouseEvent.X, mouseEvent.Y);

        var response = await frame.EvaluateScriptAsync(script).ConfigureAwait(false);
        await Task.Delay(80).ConfigureAwait(false);
        return ParseRemoteCommandResult(response?.Result as string);
    }

    private static string EscapeJavaScriptString(string value)
    {
        return (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("'", "\\'")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
    }

    private static RemoteCommandResult ParseRemoteCommandResult(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return RemoteCommandResult.Success();
        }

        try
        {
            return ParseJsonResult<RemoteCommandResult>(json) ?? RemoteCommandResult.Success();
        }
        catch
        {
            return RemoteCommandResult.Success();
        }
    }

    private static T? ParseJsonResult<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            using (var stream = new MemoryStream(bytes))
            {
                var serializer = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(T));
                return serializer.ReadObject(stream) as T;
            }
        }
        catch
        {
            return null;
        }
    }

}

internal sealed class CachedJpegFrame
{
    public CachedJpegFrame(byte[] bytes, DateTime capturedAtUtc)
    {
        Bytes = bytes;
        CapturedAtUtc = capturedAtUtc;
    }

    public byte[] Bytes { get; }

    public DateTime CapturedAtUtc { get; }
}

public sealed class CachedBitmapFrame
{
    public CachedBitmapFrame(byte[] pixels, int width, int height, int stride, DateTime capturedAtUtc)
    {
        Pixels = pixels ?? Array.Empty<byte>();
        Width = width;
        Height = height;
        Stride = stride;
        CapturedAtUtc = capturedAtUtc;
    }

    public byte[] Pixels { get; }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public DateTime CapturedAtUtc { get; }
}

 [DataContract]
public sealed class RemoteCommandResult
{
    [DataMember(Name = "ok", Order = 1)]
    public bool Ok { get; set; }

    [DataMember(Name = "editable", Order = 2)]
    public bool Editable { get; set; }

    [DataMember(Name = "multiline", Order = 3)]
    public bool Multiline { get; set; }

    [DataMember(Name = "tagName", Order = 4)]
    public string TagName { get; set; } = string.Empty;

    [DataMember(Name = "inputType", Order = 5)]
    public string InputType { get; set; } = string.Empty;

    [DataMember(Name = "value", Order = 6)]
    public string Value { get; set; } = string.Empty;

    [DataMember(Name = "targetPath", Order = 7)]
    public string TargetPath { get; set; } = string.Empty;

    [DataMember(Name = "clickablePath", Order = 8)]
    public string ClickablePath { get; set; } = string.Empty;

    [DataMember(Name = "navigationUrl", Order = 9)]
    public string NavigationUrl { get; set; } = string.Empty;

    public static RemoteCommandResult Success()
    {
        return new RemoteCommandResult { Ok = true };
    }

    public static RemoteCommandResult Failure()
    {
        return new RemoteCommandResult { Ok = false };
    }
}

[DataContract]
public sealed class BrowserDirectVideoOverlayState
{
    public static BrowserDirectVideoOverlayState None => new BrowserDirectVideoOverlayState();

    [DataMember(Name = "detected", Order = 1)]
    public bool Detected { get; set; }

    [DataMember(Name = "ready", Order = 2)]
    public bool Ready { get; set; }

    [DataMember(Name = "provider", Order = 3)]
    public string Provider { get; set; } = string.Empty;

    [DataMember(Name = "sourceUrl", Order = 4)]
    public string SourceUrl { get; set; } = string.Empty;

    [DataMember(Name = "left", Order = 5)]
    public double Left { get; set; }

    [DataMember(Name = "top", Order = 6)]
    public double Top { get; set; }

    [DataMember(Name = "width", Order = 7)]
    public double Width { get; set; }

    [DataMember(Name = "height", Order = 8)]
    public double Height { get; set; }

    [DataMember(Name = "normalizedLeft", Order = 9)]
    public double NormalizedLeft { get; set; }

    [DataMember(Name = "normalizedTop", Order = 10)]
    public double NormalizedTop { get; set; }

    [DataMember(Name = "normalizedWidth", Order = 11)]
    public double NormalizedWidth { get; set; }

    [DataMember(Name = "normalizedHeight", Order = 12)]
    public double NormalizedHeight { get; set; }

    [DataMember(Name = "blocked", Order = 13)]
    public bool Blocked { get; set; }

    [DataMember(Name = "embedded", Order = 14)]
    public bool Embedded { get; set; }

    [DataMember(Name = "pageUrl", Order = 15)]
    public string PageUrl { get; set; } = string.Empty;

    [DataMember(Name = "pageHost", Order = 16)]
    public string PageHost { get; set; } = string.Empty;

    [DataMember(Name = "pageTitle", Order = 17)]
    public string PageTitle { get; set; } = string.Empty;

    [DataMember(Name = "detectionReason", Order = 18)]
    public string DetectionReason { get; set; } = string.Empty;

    public DateTime UpdatedUtc { get; set; }
}

public sealed class RemoteCursorState
{
    public int X { get; private set; } = 640;

    public int Y { get; private set; } = 360;

    public void UpdateFromClient(int? x, int? y, int width, int height)
    {
        if (x.HasValue)
        {
            X = x.Value;
        }

        if (y.HasValue)
        {
            Y = y.Value;
        }

        X = Math.Max(0, Math.Min(width - 1, X));
        Y = Math.Max(0, Math.Min(height - 1, Y));
    }

    public MouseEvent ToMouseEvent()
    {
        return new MouseEvent(X, Y, CefEventFlags.None);
    }
}
