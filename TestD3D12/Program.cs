using TestD3D12.Graphics;
using TestD3D12.Logging;
using TestD3D12.Windowing;
using Vortice.Direct3D;
using static Vortice.Direct3D12.D3D12;

Logger.LogInformation("Adding FirstChanceException handler");
AppDomain.CurrentDomain.FirstChanceException += static (sender, e) =>
{
    Logger.LogWarning($"[FirstChanceException]: {e.Exception}");
    Logger.LogInformation($"SDL_GetError: {SDL.SDL3.SDL_GetError()}");
};

Logger.LogInformation("Checking for D3D12 FeatureLevel 12_0");
if (!IsSupported(FeatureLevel.Level_12_0))
{
    Logger.LogCritical("No D3D12 FeatureLevel 12_0; likely could not load d3d12.dll");
    throw new NotSupportedException();
}
Logger.LogInformation("Founds support and loaded D3D12 FeatureLevel 12_0");

Logger.LogInformation("Creating test window");
using BaseWindow window = BaseWindow.CreateWindow("D3D12 Test Window", 1280, 720);
Logger.LogInformation($"Created test window with handle {window.WindowHandle:X}");

Logger.LogInformation($"Creating {nameof(D3D12Renderer)}");
using D3D12Renderer renderer = new(window);
