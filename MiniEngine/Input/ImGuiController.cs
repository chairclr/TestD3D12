using ImGuiNET;
using MiniEngine.Windowing;
using SDL;

namespace MiniEngine.Input;

internal class ImGuiController
{
    private readonly SDLWindow _window;
    private readonly nint _imGuiContext;

    public ImGuiController(SDLWindow window, nint imGuiContext)
    {
        _window = window;
        _imGuiContext = imGuiContext;
    }

    public void NewFrame(float deltaTime)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        io.DisplaySize = _window.Size;
        io.DeltaTime = deltaTime;

        ImGui.SetCurrentContext(_imGuiContext);
        ImGui.NewFrame();
    }

    public void EndFrame()
    {
        ImGui.EndFrame();
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
            case SDL_EventType.SDL_EVENT_TEXT_INPUT:
                {
                    io.AddInputCharactersUTF8(@event.text.GetText());
                    return true;
                }
            case SDL_EventType.SDL_EVENT_KEY_UP:
            case SDL_EventType.SDL_EVENT_KEY_DOWN:
                {
                    ImGuiKey key = SDLKeycdeToImGui(@event.key.key, @event.key.scancode);

                    if (key == ImGuiKey.None)
                    {
                        return false;
                    }

                    io.AddKeyEvent(ImGuiKey.ModCtrl, (@event.key.mod & SDL_Keymod.SDL_KMOD_CTRL) != 0);
                    io.AddKeyEvent(ImGuiKey.ModShift, (@event.key.mod & SDL_Keymod.SDL_KMOD_SHIFT) != 0);
                    io.AddKeyEvent(ImGuiKey.ModAlt, (@event.key.mod & SDL_Keymod.SDL_KMOD_ALT) != 0);
                    io.AddKeyEvent(ImGuiKey.ModSuper, (@event.key.mod & SDL_Keymod.SDL_KMOD_GUI) != 0);

                    io.AddKeyEvent(key, @event.Type == SDL_EventType.SDL_EVENT_KEY_DOWN);
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

    // See https://github.com/ocornut/imgui/blob/4bdb0ac68539f3812dd1abb4b080624d22b46fe6/backends/imgui_impl_sdl3.cpp#L171
    private static ImGuiKey SDLKeycdeToImGui(SDL_Keycode keycode, SDL_Scancode scancode)
    {
        // Keypad doesn't have individual key values in SDL3
        switch (scancode)
        {
            case SDL_Scancode.SDL_SCANCODE_KP_0: return ImGuiKey.Keypad0;
            case SDL_Scancode.SDL_SCANCODE_KP_1: return ImGuiKey.Keypad1;
            case SDL_Scancode.SDL_SCANCODE_KP_2: return ImGuiKey.Keypad2;
            case SDL_Scancode.SDL_SCANCODE_KP_3: return ImGuiKey.Keypad3;
            case SDL_Scancode.SDL_SCANCODE_KP_4: return ImGuiKey.Keypad4;
            case SDL_Scancode.SDL_SCANCODE_KP_5: return ImGuiKey.Keypad5;
            case SDL_Scancode.SDL_SCANCODE_KP_6: return ImGuiKey.Keypad6;
            case SDL_Scancode.SDL_SCANCODE_KP_7: return ImGuiKey.Keypad7;
            case SDL_Scancode.SDL_SCANCODE_KP_8: return ImGuiKey.Keypad8;
            case SDL_Scancode.SDL_SCANCODE_KP_9: return ImGuiKey.Keypad9;
            case SDL_Scancode.SDL_SCANCODE_KP_PERIOD: return ImGuiKey.KeypadDecimal;
            case SDL_Scancode.SDL_SCANCODE_KP_DIVIDE: return ImGuiKey.KeypadDivide;
            case SDL_Scancode.SDL_SCANCODE_KP_MULTIPLY: return ImGuiKey.KeypadMultiply;
            case SDL_Scancode.SDL_SCANCODE_KP_MINUS: return ImGuiKey.KeypadSubtract;
            case SDL_Scancode.SDL_SCANCODE_KP_PLUS: return ImGuiKey.KeypadAdd;
            case SDL_Scancode.SDL_SCANCODE_KP_ENTER: return ImGuiKey.KeypadEnter;
            case SDL_Scancode.SDL_SCANCODE_KP_EQUALS: return ImGuiKey.KeypadEqual;
            default: break;
        }

        switch (keycode)
        {
            case SDL_Keycode.SDLK_TAB: return ImGuiKey.Tab;
            case SDL_Keycode.SDLK_LEFT: return ImGuiKey.LeftArrow;
            case SDL_Keycode.SDLK_RIGHT: return ImGuiKey.RightArrow;
            case SDL_Keycode.SDLK_UP: return ImGuiKey.UpArrow;
            case SDL_Keycode.SDLK_DOWN: return ImGuiKey.DownArrow;
            case SDL_Keycode.SDLK_PAGEUP: return ImGuiKey.PageUp;
            case SDL_Keycode.SDLK_PAGEDOWN: return ImGuiKey.PageDown;
            case SDL_Keycode.SDLK_HOME: return ImGuiKey.Home;
            case SDL_Keycode.SDLK_END: return ImGuiKey.End;
            case SDL_Keycode.SDLK_INSERT: return ImGuiKey.Insert;
            case SDL_Keycode.SDLK_DELETE: return ImGuiKey.Delete;
            case SDL_Keycode.SDLK_BACKSPACE: return ImGuiKey.Backspace;
            case SDL_Keycode.SDLK_SPACE: return ImGuiKey.Space;
            case SDL_Keycode.SDLK_RETURN: return ImGuiKey.Enter;
            case SDL_Keycode.SDLK_ESCAPE: return ImGuiKey.Escape;
            case SDL_Keycode.SDLK_APOSTROPHE: return ImGuiKey.Apostrophe;
            case SDL_Keycode.SDLK_COMMA: return ImGuiKey.Comma;
            case SDL_Keycode.SDLK_MINUS: return ImGuiKey.Minus;
            case SDL_Keycode.SDLK_PERIOD: return ImGuiKey.Period;
            case SDL_Keycode.SDLK_SLASH: return ImGuiKey.Slash;
            case SDL_Keycode.SDLK_SEMICOLON: return ImGuiKey.Semicolon;
            case SDL_Keycode.SDLK_EQUALS: return ImGuiKey.Equal;
            case SDL_Keycode.SDLK_LEFTBRACKET: return ImGuiKey.LeftBracket;
            case SDL_Keycode.SDLK_BACKSLASH: return ImGuiKey.Backslash;
            case SDL_Keycode.SDLK_RIGHTBRACKET: return ImGuiKey.RightBracket;
            case SDL_Keycode.SDLK_GRAVE: return ImGuiKey.GraveAccent;
            case SDL_Keycode.SDLK_CAPSLOCK: return ImGuiKey.CapsLock;
            case SDL_Keycode.SDLK_SCROLLLOCK: return ImGuiKey.ScrollLock;
            case SDL_Keycode.SDLK_NUMLOCKCLEAR: return ImGuiKey.NumLock;
            case SDL_Keycode.SDLK_PRINTSCREEN: return ImGuiKey.PrintScreen;
            case SDL_Keycode.SDLK_PAUSE: return ImGuiKey.Pause;
            case SDL_Keycode.SDLK_LCTRL: return ImGuiKey.LeftCtrl;
            case SDL_Keycode.SDLK_LSHIFT: return ImGuiKey.LeftShift;
            case SDL_Keycode.SDLK_LALT: return ImGuiKey.LeftAlt;
            case SDL_Keycode.SDLK_LGUI: return ImGuiKey.LeftSuper;
            case SDL_Keycode.SDLK_RCTRL: return ImGuiKey.RightCtrl;
            case SDL_Keycode.SDLK_RSHIFT: return ImGuiKey.RightShift;
            case SDL_Keycode.SDLK_RALT: return ImGuiKey.RightAlt;
            case SDL_Keycode.SDLK_RGUI: return ImGuiKey.RightSuper;
            case SDL_Keycode.SDLK_APPLICATION: return ImGuiKey.Menu;
            case SDL_Keycode.SDLK_0: return ImGuiKey._0;
            case SDL_Keycode.SDLK_1: return ImGuiKey._1;
            case SDL_Keycode.SDLK_2: return ImGuiKey._2;
            case SDL_Keycode.SDLK_3: return ImGuiKey._3;
            case SDL_Keycode.SDLK_4: return ImGuiKey._4;
            case SDL_Keycode.SDLK_5: return ImGuiKey._5;
            case SDL_Keycode.SDLK_6: return ImGuiKey._6;
            case SDL_Keycode.SDLK_7: return ImGuiKey._7;
            case SDL_Keycode.SDLK_8: return ImGuiKey._8;
            case SDL_Keycode.SDLK_9: return ImGuiKey._9;
            case SDL_Keycode.SDLK_A: return ImGuiKey.A;
            case SDL_Keycode.SDLK_B: return ImGuiKey.B;
            case SDL_Keycode.SDLK_C: return ImGuiKey.C;
            case SDL_Keycode.SDLK_D: return ImGuiKey.D;
            case SDL_Keycode.SDLK_E: return ImGuiKey.E;
            case SDL_Keycode.SDLK_F: return ImGuiKey.F;
            case SDL_Keycode.SDLK_G: return ImGuiKey.G;
            case SDL_Keycode.SDLK_H: return ImGuiKey.H;
            case SDL_Keycode.SDLK_I: return ImGuiKey.I;
            case SDL_Keycode.SDLK_J: return ImGuiKey.J;
            case SDL_Keycode.SDLK_K: return ImGuiKey.K;
            case SDL_Keycode.SDLK_L: return ImGuiKey.L;
            case SDL_Keycode.SDLK_M: return ImGuiKey.M;
            case SDL_Keycode.SDLK_N: return ImGuiKey.N;
            case SDL_Keycode.SDLK_O: return ImGuiKey.O;
            case SDL_Keycode.SDLK_P: return ImGuiKey.P;
            case SDL_Keycode.SDLK_Q: return ImGuiKey.Q;
            case SDL_Keycode.SDLK_R: return ImGuiKey.R;
            case SDL_Keycode.SDLK_S: return ImGuiKey.S;
            case SDL_Keycode.SDLK_T: return ImGuiKey.T;
            case SDL_Keycode.SDLK_U: return ImGuiKey.U;
            case SDL_Keycode.SDLK_V: return ImGuiKey.V;
            case SDL_Keycode.SDLK_W: return ImGuiKey.W;
            case SDL_Keycode.SDLK_X: return ImGuiKey.X;
            case SDL_Keycode.SDLK_Y: return ImGuiKey.Y;
            case SDL_Keycode.SDLK_Z: return ImGuiKey.Z;
            case SDL_Keycode.SDLK_F1: return ImGuiKey.F1;
            case SDL_Keycode.SDLK_F2: return ImGuiKey.F2;
            case SDL_Keycode.SDLK_F3: return ImGuiKey.F3;
            case SDL_Keycode.SDLK_F4: return ImGuiKey.F4;
            case SDL_Keycode.SDLK_F5: return ImGuiKey.F5;
            case SDL_Keycode.SDLK_F6: return ImGuiKey.F6;
            case SDL_Keycode.SDLK_F7: return ImGuiKey.F7;
            case SDL_Keycode.SDLK_F8: return ImGuiKey.F8;
            case SDL_Keycode.SDLK_F9: return ImGuiKey.F9;
            case SDL_Keycode.SDLK_F10: return ImGuiKey.F10;
            case SDL_Keycode.SDLK_F11: return ImGuiKey.F11;
            case SDL_Keycode.SDLK_F12: return ImGuiKey.F12;
            case SDL_Keycode.SDLK_F13: return ImGuiKey.F13;
            case SDL_Keycode.SDLK_F14: return ImGuiKey.F14;
            case SDL_Keycode.SDLK_F15: return ImGuiKey.F15;
            case SDL_Keycode.SDLK_F16: return ImGuiKey.F16;
            case SDL_Keycode.SDLK_F17: return ImGuiKey.F17;
            case SDL_Keycode.SDLK_F18: return ImGuiKey.F18;
            case SDL_Keycode.SDLK_F19: return ImGuiKey.F19;
            case SDL_Keycode.SDLK_F20: return ImGuiKey.F20;
            case SDL_Keycode.SDLK_F21: return ImGuiKey.F21;
            case SDL_Keycode.SDLK_F22: return ImGuiKey.F22;
            case SDL_Keycode.SDLK_F23: return ImGuiKey.F23;
            case SDL_Keycode.SDLK_F24: return ImGuiKey.F24;
            case SDL_Keycode.SDLK_AC_BACK: return ImGuiKey.AppBack;
            case SDL_Keycode.SDLK_AC_FORWARD: return ImGuiKey.AppForward;
            default: break;
        }

        // Fallback to scancode
        switch (scancode)
        {
            case SDL_Scancode.SDL_SCANCODE_GRAVE: return ImGuiKey.GraveAccent;
            case SDL_Scancode.SDL_SCANCODE_MINUS: return ImGuiKey.Minus;
            case SDL_Scancode.SDL_SCANCODE_EQUALS: return ImGuiKey.Equal;
            case SDL_Scancode.SDL_SCANCODE_LEFTBRACKET: return ImGuiKey.LeftBracket;
            case SDL_Scancode.SDL_SCANCODE_RIGHTBRACKET: return ImGuiKey.RightBracket;
            //case SDL_Scancode.SDL_SCANCODE_NONUSBACKSLASH: return ImGuiKey.Oem102;
            case SDL_Scancode.SDL_SCANCODE_BACKSLASH: return ImGuiKey.Backslash;
            case SDL_Scancode.SDL_SCANCODE_SEMICOLON: return ImGuiKey.Semicolon;
            case SDL_Scancode.SDL_SCANCODE_APOSTROPHE: return ImGuiKey.Apostrophe;
            case SDL_Scancode.SDL_SCANCODE_COMMA: return ImGuiKey.Comma;
            case SDL_Scancode.SDL_SCANCODE_PERIOD: return ImGuiKey.Period;
            case SDL_Scancode.SDL_SCANCODE_SLASH: return ImGuiKey.Slash;
            default: break;
        }

        return ImGuiKey.None;
    }
}