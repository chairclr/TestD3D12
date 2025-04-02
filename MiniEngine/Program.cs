using MiniEngine.Graphics;
using MiniEngine.Logging;
using MiniEngine.Windowing;
using Vortice.Direct3D;
using static Vortice.Direct3D12.D3D12;

if (OperatingSystem.IsLinux())
{
    // Custom vkd3d is only compiled to support x11, so we must hint to SDL that we need the x11 video driver
    //
    // SDL_SetHintWithPriority lets us override environment variables
    // https://wiki.libsdl.org/SDL2/SDL_HINT_VIDEODRIVER
    Log.LogInfo($"Linux detected, hinting {nameof(SDL.SDL3.SDL_HINT_VIDEO_DRIVER)} to x11");
    SDL.SDL3.SDL_SetHintWithPriority(SDL.SDL3.SDL_HINT_VIDEO_DRIVER, "x11", SDL.SDL_HintPriority.SDL_HINT_OVERRIDE);
}

if (Environment.GetEnvironmentVariable("MINIENGINE_FCE") == "1")
{
    Log.LogInfo("Adding FirstChanceException handler");
    AppDomain.CurrentDomain.FirstChanceException += static (sender, e) =>
    {
        Log.LogWarn($"[FirstChanceException]: {e.Exception}");
        Log.LogInfo($"SDL_GetError: {SDL.SDL3.SDL_GetError()}");
    };
}

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