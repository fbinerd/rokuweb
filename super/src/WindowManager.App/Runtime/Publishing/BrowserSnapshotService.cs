using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CefSharp;
using CefSharp.Wpf;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowManager.App.Runtime;

namespace WindowManager.App.Runtime.Publishing;

public sealed class BrowserSnapshotService
{
    private readonly ConcurrentDictionary<Guid, ChromiumWebBrowser> _browsers = new ConcurrentDictionary<Guid, ChromiumWebBrowser>();
    private readonly ConcurrentDictionary<Guid, RemoteCursorState> _cursorStates = new ConcurrentDictionary<Guid, RemoteCursorState>();

    public void Register(Guid windowId, ChromiumWebBrowser browser)
    {
        _browsers[windowId] = browser;
        _cursorStates.TryAdd(windowId, new RemoteCursorState());
    }

    public void Unregister(Guid windowId)
    {
        _browsers.TryRemove(windowId, out _);
        _cursorStates.TryRemove(windowId, out _);
    }

    public async Task<byte[]?> CaptureJpegAsync(Guid windowId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_browsers.TryGetValue(windowId, out var browser))
        {
            return null;
        }

        try
        {
            var jpegBytes = await browser.Dispatcher.InvokeAsync(() =>
            {
                var width = Math.Max(1, (int)Math.Ceiling(browser.ActualWidth));
                var height = Math.Max(1, (int)Math.Ceiling(browser.ActualHeight));

                if (width <= 1 || height <= 1)
                {
                    return (byte[]?)null;
                }

                browser.Measure(new Size(width, height));
                browser.Arrange(new Rect(0, 0, width, height));
                browser.UpdateLayout();

                var renderTarget = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                renderTarget.Render(browser);

                var encoder = new JpegBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(renderTarget));

                using (var stream = new MemoryStream())
                {
                    encoder.Save(stream);
                    return stream.ToArray();
                }
            });

            return jpegBytes;
        }
        catch
        {
            return null;
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
                    await HighlightElementAtPointAsync(frame, cursor.X, cursor.Y).ConfigureAwait(true);
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
                    AppLog.Write("RokuControl", string.Format("Mouse click => x={0}, y={1}, ok={2}, editable={3}, tag={4}, type={5}, target={6}, clickable={7}, nav={8}", cursor.X, cursor.Y, clickResult.Ok, clickResult.Editable, clickResult.TagName, clickResult.InputType, clickResult.TargetPath, clickResult.ClickablePath, clickResult.NavigationUrl));
                    return clickResult;
                case "set-text":
                    var setTextResult = await SetTextAtFocusedElementAsync(frame, cursor.X, cursor.Y, text ?? string.Empty).ConfigureAwait(true);
                    AppLog.Write("RokuControl", string.Format("Texto aplicado ao CEF. ok={0}, editable={1}, tag={2}, type={3}, valueLength={4}, finalValue={5}", setTextResult.Ok, setTextResult.Editable, setTextResult.TagName, setTextResult.InputType, (text ?? string.Empty).Length, setTextResult.Value));
                    return setTextResult;
                case "back":
                    SendKey(host, 0x1B);
                    AppLog.Write("RokuControl", "Escape enviado ao CEF.");
                    return RemoteCommandResult.Success();
                case "play":
                case "tab":
                    SendKey(host, 0x09);
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
