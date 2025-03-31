using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace MiniEngine.Platform;

[SupportedOSPlatform("linux")]
public class UnixAutoResetEvent : WaitHandle
{
    private const int EFD_NONBLOCK = 0x800;
    private const int EFD_CLOEXEC = 0x1;
    private readonly SafeEventFdHandle _eventFd;

    [DllImport("libc", SetLastError = true)]
    private static extern int eventfd(uint initval, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int write(int fd, ref ulong value, IntPtr size);

    [DllImport("libc", SetLastError = true)]
    private static extern int read(int fd, out ulong value, IntPtr size);

    public UnixAutoResetEvent(bool initialState)
    {
        int fd = eventfd((uint)(initialState ? 1 : 0), EFD_NONBLOCK | EFD_CLOEXEC);
        if (fd < 0)
        {
            throw new InvalidOperationException("Failed to create eventfd");
        }

        _eventFd = new SafeEventFdHandle(fd);
        SafeWaitHandle = new SafeWaitHandle(_eventFd.DangerousGetHandle(), true);
    }

    public bool Set()
    {
        ulong value = 1;
        int result = write(_eventFd.DangerousGetHandle().ToInt32(), ref value, sizeof(ulong));
        return result >= 0;
    }

    public override bool WaitOne(int millisecondsTimeout, bool exitContext)
    {
        int result;
        DateTime startTime = DateTime.UtcNow;

        // Do while loop here because chatgpt said so
        // it kinda makes sense though so I'll leave it
        do
        {
            if (_eventFd.IsClosed || _eventFd.IsInvalid)
            {
                return false;
            }

            result = read(_eventFd.DangerousGetHandle().ToInt32(), out ulong _, sizeof(ulong));
            if (result >= 0)
            {
                return true;
            }

            int err = Marshal.GetLastPInvokeError();
            if (err != 11 /*EAGAIN*/ && err != 4 /*EINTR*/)
            {
                throw new InvalidOperationException("Failed to read eventfd");
            }

            if (millisecondsTimeout >= 0 && (DateTime.UtcNow - startTime).TotalMilliseconds >= millisecondsTimeout)
            {
                return false;
            }

            Thread.Sleep(1);
        } while (true);
    }

    public override bool WaitOne(int millisecondsTimeout)
    {
        return WaitOne(millisecondsTimeout, false);
    }

    public override bool WaitOne()
    {
        return WaitOne(-1, false);
    }

    protected override void Dispose(bool explicitDisposing)
    {
        if (!_eventFd.IsInvalid)
        {
            _eventFd.Dispose();
        }

        base.Dispose(explicitDisposing);
    }
}

public class SafeEventFdHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    public SafeEventFdHandle(int fd) : base(true)
    {
        SetHandle(fd);
    }

    protected override bool ReleaseHandle()
    {
        return close(handle.ToInt32()) == 0;
    }
}
