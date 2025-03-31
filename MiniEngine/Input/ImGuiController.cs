using ImGuiNET;
using SDL;
using MiniEngine.Windowing;

namespace MiniEngine.Input;

internal class ImGuiController
{
    private readonly BaseWindow _window;
    private readonly nint _imGuiContext;

    public ImGuiController(BaseWindow window, nint imGuiContext)
    {
        _window = window;
        _imGuiContext = imGuiContext;
    }

    public void NewFrame()
    {
        ImGuiIOPtr io = ImGui.GetIO();
        io.DisplaySize = _window.Size;

        ImGui.SetCurrentContext(_imGuiContext);
        ImGui.NewFrame();
    }

    public bool HandleEvent(SDL_Event @event)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        switch (@event.Type)
        {
            case SDL_EventType.SDL_EVENT_MOUSE_MOTION:
                {
                    io.AddMouseSourceEvent(ImGuiMouseSource.Mouse);
                    io.AddMousePosEvent(@event.motion.x, @event.motion.y);
                    return true;
                }
            case SDL_EventType.SDL_EVENT_MOUSE_WHEEL:
                {
                    io.AddMouseSourceEvent(ImGuiMouseSource.Mouse);
                    io.AddMouseWheelEvent(-@event.wheel.x, @event.wheel.y);
                    return true;
                }
            case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP:
            case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:
                {
                    int mouse_button = SDLMouseButtonToImGui(@event.button.Button);
                    if (mouse_button == -1)
                    {
                        return false;
                    }

                    io.AddMouseSourceEvent(ImGuiMouseSource.Mouse);
                    io.AddMouseButtonEvent(mouse_button, @event.Type == SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN);
                    return true;
                }
            case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST:
            case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_GAINED:
                {
                    io.AddFocusEvent(@event.Type == SDL_EventType.SDL_EVENT_WINDOW_FOCUS_GAINED);
                    return true;
                }
        }

        return false;
    }

    private static int SDLMouseButtonToImGui(SDLButton button)
    {
        switch (button)
        {
            case SDLButton.SDL_BUTTON_LEFT:
                return 0;
            case SDLButton.SDL_BUTTON_RIGHT:
                return 1;
            case SDLButton.SDL_BUTTON_MIDDLE:
                return 2;
            case SDLButton.SDL_BUTTON_X1:
                return 3;
            case SDLButton.SDL_BUTTON_X2:
                return 4;
        }

        return -1;
    }
}
