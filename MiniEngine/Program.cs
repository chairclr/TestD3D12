using MiniEngine.Graphics;
using MiniEngine.Logging;
using MiniEngine.Windowing;
using Vortice.Direct3D;
using static Vortice.Direct3D12.D3D12;

Environment.SetEnvironmentVariable("SDL_VIDEODRIVER", "x11");

#if false
Log.LogInfo("Adding FirstChanceException handler");
AppDomain.CurrentDomain.FirstChanceException += static (sender, e) =>
{
    Log.LogWarn($"[FirstChanceException]: {e.Exception}");
    Log.LogInfo($"SDL_GetError: {SDL.SDL3.SDL_GetError()}");
};
#endif

Log.LogInfo("Checking for D3D12 FeatureLevel 12_0");
if (!IsSupported(FeatureLevel.Level_12_0))
{
    Log.LogCrit("No D3D12 FeatureLevel 12_0; likely could not load d3d12.dll");
    throw new NotSupportedException();
}
Log.LogInfo("Founds support and loaded D3D12 FeatureLevel 12_0");

Log.LogInfo("Creating test window");
using BaseWindow window = BaseWindow.CreateWindow("MiniEngine Main Window", 1920, 1080);
Log.LogInfo($"Created test window with handle {window.WindowHandle:X}");

Log.LogInfo($"Creating {nameof(D3D12Renderer)}");
using D3D12Renderer renderer = new(window);
