using System.Runtime.InteropServices;

namespace TestD3D12.Platform;

public static class PlatformHelper
{
    public static WaitHandle CreateAutoResetEvent(bool initialState)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new UnixAutoResetEvent(false);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new AutoResetEvent(false);
        }
        else
        {
            throw new PlatformNotSupportedException();
        }
    }
}
