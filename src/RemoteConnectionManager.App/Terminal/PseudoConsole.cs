using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace RemoteConnectionManager.App.Terminal;

/// <summary>
/// Owns a HPCON (pseudoconsole) handle returned from CreatePseudoConsole. Resize feeds new
/// column/row counts into the pseudoconsole; Dispose closes the handle, which tears down the
/// child process if it is still attached.
/// </summary>
internal sealed class PseudoConsole : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    public IntPtr Handle => _handle;
    public bool IsDisposed => _disposed;

    private PseudoConsole(IntPtr handle) { _handle = handle; }

    public static PseudoConsole Create(SafeFileHandle inputReadSide, SafeFileHandle outputWriteSide, int columns, int rows)
    {
        if (columns <= 0) columns = 80;
        if (rows <= 0) rows = 30;
        var size = new NativeMethods.COORD { X = (short)columns, Y = (short)rows };
        var hr = NativeMethods.CreatePseudoConsole(size, inputReadSide, outputWriteSide, 0, out var hPC);
        if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole failed");
        return new PseudoConsole(hPC);
    }

    public void Resize(int columns, int rows)
    {
        if (_disposed || _handle == IntPtr.Zero || columns <= 0 || rows <= 0) return;
        NativeMethods.ResizePseudoConsole(_handle, new NativeMethods.COORD { X = (short)columns, Y = (short)rows });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.ClosePseudoConsole(_handle);
            _handle = IntPtr.Zero;
        }
    }
}

/// <summary>
/// One half of the pseudoconsole's two pipe duplex. Two instances: input (renderer to child
/// stdin) and output (child stdout to renderer).
/// </summary>
internal sealed class PseudoConsolePipe : IDisposable
{
    public SafeFileHandle ReadSide { get; }
    public SafeFileHandle WriteSide { get; }

    public PseudoConsolePipe()
    {
        if (!NativeMethods.CreatePipe(out var read, out var write, IntPtr.Zero, 0))
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "CreatePipe failed");
        ReadSide = read;
        WriteSide = write;
    }

    public void Dispose()
    {
        ReadSide.Dispose();
        WriteSide.Dispose();
    }
}
