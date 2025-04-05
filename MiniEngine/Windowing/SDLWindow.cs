using System.Numerics;
using MiniEngine.Logging;
using SDL;
using static SDL.SDL3;

namespace MiniEngine.Windowing;

public unsafe class SDLWindow : IDisposable
{
    private bool _disposed;

    internal SDL_Window* SDLWindowHandle { get; }

    public nint WindowHandle
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return (nint)SDLWindowHandle;
            }

            return SDL_GetPointerProperty(SDL_GetWindowProperties(SDLWindowHandle), SDL_PROP_WINDOW_WIN32_HWND_POINTER, nint.Zero);
        }
    }

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

    private SDLWindow(SDL_Window* sdlWindow)
    {
        SDLWindowHandle = sdlWindow;
    }

    public static SDLWindow CreateWindow(string title, int width, int height, bool resizable = true, bool hidpi = true)
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

        return new SDLWindow(sdlWindow);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            Log.LogInfo("Disposing BaseWindow");
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