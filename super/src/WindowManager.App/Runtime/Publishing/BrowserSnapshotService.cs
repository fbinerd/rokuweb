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
    private static readonly TimeSpan CachedFrameLifetime = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan BackgroundCaptureInterval = TimeSpan.FromMilliseconds(66);
    private readonly ConcurrentDictionary<Guid, ChromiumWebBrowser> _browsers = new ConcurrentDictionary<Guid, ChromiumWebBrowser>();
    private readonly ConcurrentDictionary<Guid, CachedBitmapFrame> _cachedBitmapFrames = new ConcurrentDictionary<Guid, CachedBitmapFrame>();
    private readonly ConcurrentDictionary<Guid, CachedJpegFrame> _cachedFrames = new ConcurrentDictionary<Guid, CachedJpegFrame>();
    private readonly ConcurrentDictionary<Guid, Task<byte[]?>> _captureTasks = new ConcurrentDictionary<Guid, Task<byte[]?>>();
    private readonly ConcurrentDictionary<Guid, Task<CachedBitmapFrame?>> _bitmapCaptureTasks = new ConcurrentDictionary<Guid, Task<CachedBitmapFrame?>>();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _captureLoops = new ConcurrentDictionary<Guid, CancellationTokenSource>();
    private readonly ConcurrentDictionary<Guid, RemoteCursorState> _cursorStates = new ConcurrentDictionary<Guid, RemoteCursorState>();

    public void Register(Guid windowId, ChromiumWebBrowser browser)
    {
        _browsers[windowId] = browser;
        _cachedBitmapFrames.TryRemove(windowId, out _);
        _cachedFrames.TryRemove(windowId, out _);
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
            AppLog.Write("RokuControl", string.Format("Recebido comando '{0}' para janela {1}", normalizedCommand, windowId.ToString("N")));

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
                    AppLog.Write("RokuControl", string.Format("Mouse move => x={0}, y={1}", cursor.X, cursor.Y));
                    return RemoteCommandResult.Success();
                case "click":
                case "ok":
                case "select":
                    host.SendMouseMoveEvent(cursor.ToMouseEvent(), false);
                    host.SendMouseClickEvent(cursor.ToMouseEvent(), MouseButtonType.Left, false, 1);
                    host.SendMouseClickEvent(cursor.ToMouseEvent(), MouseButtonType.Left, true, 1);
                    var clickResult = await ClickElementAtPointAsync(frame, cursor.X, cursor.Y).ConfigureAwait(true);
                    if (!string.IsNullOrWhiteSpace(clickResult.NavigationUrl))
                    {
                        browser.Load(clickResult.NavigationUrl);
                    }

                    InvalidateCapture(windowId);
                    AppLog.Write("RokuControl", string.Format("Mouse click => x={0}, y={1}, ok={2}, editable={3}, tag={4}, type={5}, target={6}, clickable={7}, nav={8}", cursor.X, cursor.Y, clickResult.Ok, clickResult.Editable, clickResult.TagName, clickResult.InputType, clickResult.TargetPath, clickResult.ClickablePath, clickResult.NavigationUrl));
                    return clickResult;
                case "set-text":
                    var setTextResult = await SetTextAtFocusedElementAsync(frame, cursor.X, cursor.Y, text ?? string.Empty).ConfigureAwait(true);
                    InvalidateCapture(windowId);
                    AppLog.Write("RokuControl", string.Format("Texto aplicado ao CEF. ok={0}, editable={1}, tag={2}, type={3}, valueLength={4}, finalValue={5}", setTextResult.Ok, setTextResult.Editable, setTextResult.TagName, setTextResult.InputType, (text ?? string.Empty).Length, setTextResult.Value));
                    return setTextResult;
                case "back":
                    SendKey(host, 0x1B);
                    InvalidateCapture(windowId);
                    AppLog.Write("RokuControl", "Escape enviado ao CEF.");
                    return RemoteCommandResult.Success();
                case "reload":
                    browser.Reload();
                    InvalidateCapture(windowId);
                    AppLog.Write("RokuControl", "Reload enviado ao CEF.");
                    return RemoteCommandResult.Success();
                case "history-back":
                    if (cefBrowser.CanGoBack)
                    {
                        cefBrowser.GoBack();
                        InvalidateCapture(windowId);
                        AppLog.Write("RokuControl", "Historico voltar enviado ao CEF.");
                        return RemoteCommandResult.Success();
                    }

                    AppLog.Write("RokuControl", "Historico voltar ignorado; navegador sem pagina anterior.");
                    return RemoteCommandResult.Success();
                case "history-forward":
                    if (cefBrowser.CanGoForward)
                    {
                        cefBrowser.GoForward();
                        InvalidateCapture(windowId);
                        AppLog.Write("RokuControl", "Historico avancar enviado ao CEF.");
                        return RemoteCommandResult.Success();
                    }

                    AppLog.Write("RokuControl", "Historico avancar ignorado; navegador sem pagina seguinte.");
                    return RemoteCommandResult.Success();
                case "scroll-up":
                    var scrollUpResult = await ScrollPageAsync(frame, host, cursor.ToMouseEvent(), -420).ConfigureAwait(true);
                    InvalidateCapture(windowId);
                    AppLog.Write("RokuControl", string.Format("Scroll para cima enviado ao CEF. ok={0}", scrollUpResult.Ok));
                    return scrollUpResult;
                case "scroll-down":
                    var scrollDownResult = await ScrollPageAsync(frame, host, cursor.ToMouseEvent(), 420).ConfigureAwait(true);
                    InvalidateCapture(windowId);
                    AppLog.Write("RokuControl", string.Format("Scroll para baixo enviado ao CEF. ok={0}", scrollDownResult.Ok));
                    return scrollDownResult;
                case "media-seek-backward":
                    var seekBackwardResult = await SeekMediaAsync(frame, host, -10).ConfigureAwait(true);
                    InvalidateCapture(windowId);
                    AppLog.Write("RokuControl", string.Format("Voltar video enviado ao CEF. ok={0}", seekBackwardResult.Ok));
                    return seekBackwardResult;
                case "media-seek-forward":
                    var seekForwardResult = await SeekMediaAsync(frame, host, 10).ConfigureAwait(true);
                    InvalidateCapture(windowId);
                    AppLog.Write("RokuControl", string.Format("Avancar video enviado ao CEF. ok={0}", seekForwardResult.Ok));
                    return seekForwardResult;
                case "enter":
                    SendKey(host, 0x0D);
                    InvalidateCapture(windowId);
                    AppLog.Write("RokuControl", "Enter enviado ao CEF.");
                    return RemoteCommandResult.Success();
                case "media-play-pause":
                    var mediaToggleResult = await ToggleMediaPlayPauseAsync(frame, host).ConfigureAwait(true);
                    InvalidateCapture(windowId);
                    AppLog.Write("RokuControl", string.Format("Play/Pause enviado ao CEF. ok={0}", mediaToggleResult.Ok));
                    return mediaToggleResult;
                case "play":
                case "tab":
                    SendKey(host, 0x09);
                    InvalidateCapture(windowId);
                    AppLog.Write("RokuControl", "Tab enviado ao CEF.");
                    return RemoteCommandResult.Success();
                case "open-fullscreen":
                    AppLog.Write("RokuControl", "Solicitacao de fullscreen recebida da Roku.");
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
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            using (var stream = new MemoryStream(bytes))
            {
                var serializer = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(RemoteCommandResult));
                return serializer.ReadObject(stream) as RemoteCommandResult ?? RemoteCommandResult.Success();
            }
        }
        catch
        {
            return RemoteCommandResult.Success();
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
