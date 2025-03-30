using System.Numerics;
using System.Runtime.InteropServices;
using SDL;
using TestD3D12.Logging;
using static SDL.SDL3;

namespace TestD3D12.Windowing;

public unsafe class BaseWindow : IDisposable
{
    private bool _disposed;

    internal SDL_Window* SDLWindowHandle { get; }

    public nint WindowHandle => (nint)SDLWindowHandle;

    public Vector2 Position
    {
        get
        {
            int x, y;
            SDL_GetWindowPosition(SDLWindowHandle, &x, &y);

            return new Vector2(x, y);
        }

        set => SDL_SetWindowPosition(SDLWindowHandle, (int)value.X, (int)value.Y);
    }

    public Vector2 Size
    {
        get
        {
            int w, h;
            SDL_GetWindowSize(SDLWindowHandle, &w, &h);

            return new Vector2(w, h);
        }

        set => SDL_SetWindowSize(SDLWindowHandle, (int)value.X, (int)value.Y);
    }

    public string Title
    {
        get => SDL_GetWindowTitle(SDLWindowHandle) ?? "";
        set => SDL_SetWindowTitle(SDLWindowHandle, value);
    }

    public float AspectRatio => Size.X / Size.Y;

    private BaseWindow(SDL_Window* sdlWindow)
    {
        SDLWindowHandle = sdlWindow;
    }

    public static BaseWindow CreateWindow(string title, int width, int height, bool resizable = true, bool hidpi = true)
    {
        ArgumentNullException.ThrowIfNull(title, nameof(title));
        ArgumentOutOfRangeException.ThrowIfNegative(width, nameof(width));
        ArgumentOutOfRangeException.ThrowIfNegative(height, nameof(height));

        SDL_WindowFlags flags = SDL_WindowFlags.SDL_WINDOW_VULKAN;

        if (resizable)
        {
            flags |= SDL_WindowFlags.SDL_WINDOW_RESIZABLE;
        }

        // TODO: Figure out what this actually does
        if (hidpi)
        {
            flags |= SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY;
        }

        SDL_Window* sdlWindow = SDL_CreateWindow(title, width, height, flags);

        if (sdlWindow is null)
        {
            throw new SDLException("Failed to create SDL_Window");
        }

        uint sex = 0;
        byte** guh = SDL_Vulkan_GetInstanceExtensions(&sex);

        for (int i = 0; i < sex; i++)
        {
            Logger.LogInformation(Marshal.PtrToStringAuto((nint)guh[i]));
        }

        return new BaseWindow(sdlWindow);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            Logger.LogInformation($"Destroying SDL Window with handle {WindowHandle:X}");
            SDL_DestroyWindow(SDLWindowHandle);

            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
