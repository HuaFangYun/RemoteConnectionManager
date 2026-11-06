using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace RemoteConnectionManager.App.Services;

/// <summary>
/// Puts secret values on the clipboard so they stay out of Windows clipboard history and cloud
/// clipboard, then clears them after <see cref="ClearAfter"/> unless something else has been
/// copied since. Never logs the value.
/// </summary>
public sealed class ClipboardGuard
{
    public static readonly TimeSpan ClearAfter = TimeSpan.FromSeconds(30);

    private readonly DispatcherTimer _timer;
    private string? _placed;

    /// <summary>Raised when a guarded value is cleared, with a short status line.</summary>
    public event EventHandler<string>? Cleared;

    public ClipboardGuard()
    {
        _timer = new DispatcherTimer { Interval = ClearAfter };
        _timer.Tick += (_, _) => ClearNow("Clipboard cleared");
    }

    /// <summary>Copies a secret. Returns false when the clipboard was busy.</summary>
    public bool CopySecret(string value)
    {
        var data = new DataObject();
        data.SetText(value, TextDataFormat.UnicodeText);
        // Documented opt outs read by clipboard history, cloud clipboard and clipboard monitors.
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));

        if (!Try(() => Clipboard.SetDataObject(data, copy: true))) return false;
        _placed = value;
        _timer.Stop();
        _timer.Start();
        return true;
    }

    /// <summary>Copies something that is not secret (names, ids, URIs). No auto clear.</summary>
    public bool CopyPlain(string value) => Try(() => Clipboard.SetText(value));

    /// <summary>Clears the clipboard now if it still holds the guarded value. Called on exit too.</summary>
    public void ClearNow(string reason = "Clipboard cleared")
    {
        _timer.Stop();
        if (_placed is null) return;
        var placed = _placed;
        _placed = null;
        string? current = null;
        if (!Try(() => current = Clipboard.ContainsText() ? Clipboard.GetText() : null)) return;
        if (current == placed && Try(Clipboard.Clear))
            Cleared?.Invoke(this, reason);
    }

    private static bool Try(Action action)
    {
        // Another process can hold the clipboard open for a moment.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                action();
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(40);
            }
            catch (ExternalException)
            {
                Thread.Sleep(40);
            }
        }
        return false;
    }
}
