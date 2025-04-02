namespace MiniEngine.Platform;

public static class PlatformHelper
{
    public static WaitHandle CreateAutoResetEvent(bool initialState)
    {
        if (OperatingSystem.IsLinux())
        {
            return new UnixAutoResetEvent(false);
        }
        else if (OperatingSystem.IsWindows())
        {
            return new AutoResetEvent(false);
        }
        else
        {
            throw new PlatformNotSupportedException();
        }
    }
}