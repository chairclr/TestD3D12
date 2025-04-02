using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace MiniEngine.Platform;

[SupportedOSPlatform("linux")]
public class UnixAutoResetEvent : WaitHandle
{
    private const int EFD_CLOEXEC = 0x1;

    private readonly int _fd;

    [DllImport("libc", SetLastError = true)]
    private static extern int eventfd(uint initval, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int write(int fd, ref ulong value, nint size);

    [DllImport("libc", SetLastError = true)]
    private static extern int read(int fd, out ulong value, nint size);

    public UnixAutoResetEvent(bool initialState)
    {
        _fd = eventfd((uint)(initialState ? 1 : 0), EFD_CLOEXEC);
        if (_fd < 0)
        {
            throw new InvalidOperationException("Failed to create eventfd");
        }

        SafeWaitHandle = new SafeWaitHandle(_fd, true);
    }

    public bool Set()
    {
        ulong value = 1;
        int result = write(_fd, ref value, sizeof(ulong));
        return result >= 0;
    }

    public override bool WaitOne(int millisecondsTimeout, bool exitContext)
    {
        // NOT IMPLEMENTED
        throw new NotImplementedException();
    }

    public override bool WaitOne(int millisecondsTimeout)
    {
        // NOT IMPLEMENTED
        throw new NotImplementedException();
    }

    public override bool WaitOne()
    {
        int err;
        while (read(_fd, out ulong _, sizeof(ulong)) < 0)
        {
            err = Marshal.GetLastPInvokeError();

            if (err != 11 /*EAGAIN*/ && err != 4 /*EINTR*/)
            {
                throw new InvalidOperationException("Failed to read eventfd");
            }
        }

        return true;
    }

    protected override void Dispose(bool explicitDisposing)
    {
        close(_fd);

        // NOTE: Do not call base.Dispose, as it attempts to dispose of the fd in an imporper internal way
    }
}