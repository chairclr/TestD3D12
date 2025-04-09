namespace MiniEngine.Windowing;

/// <summary>
/// Appends SDL_GetError() to the exception message
/// </summary>
public class SDLException : Exception
{
    public SDLException(string message)
        : base($"{message}: {SDL.SDL3.SDL_GetError()}")
    {

    }
}