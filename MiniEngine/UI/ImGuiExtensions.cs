using System.Numerics;
using ImGuiNET;

namespace MiniEngine.UI;

public class ImGuiExtensions
{
    private record struct ImageViewState(float Zoom, Vector2 PanOffset);
    private static readonly Dictionary<string, ImageViewState> ImageViewStates = [];

    /// <summary>
    /// Draws an image that can be panned and zoomed
    /// </summary>
    public static void ZoomableImage(string name, nint textureId, Vector2 viewSize, Vector2 textureSize)
    {
        if (!ImageViewStates.TryGetValue(name, out ImageViewState state))
        {
            // On first call, we fit the textureSize into the viewSize on the larger axis
            float scaleX = viewSize.X / textureSize.X;
            float scaleY = viewSize.Y / textureSize.Y;

            float defaultFitZoom = MathF.Min(scaleX, scaleY);
            Vector2 displaySize = textureSize * defaultFitZoom;
            Vector2 defaultFitPan = (viewSize - displaySize) * 0.5f;

            state = new ImageViewState(defaultFitZoom, defaultFitPan);
        }

        ImGui.Text($"{name} {textureSize.X}x{textureSize.Y}");
        ImGui.BeginChild(name, viewSize, ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoMove);

        ImGuiIOPtr io = ImGui.GetIO();
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        Vector2 windowPos = ImGui.GetCursorScreenPos();
        Vector2 mousePos = ImGui.GetIO().MousePos;
        Vector2 localMouse = mousePos - windowPos;

        float zoom = state.Zoom;
        Vector2 pan = state.PanOffset;

        if (ImGui.IsWindowHovered() && io.KeyCtrl && io.MouseWheel != 0)
        {
            float prevZoom = zoom;
            zoom *= 1.0f + (io.MouseWheel * 0.1f);
            zoom = Math.Clamp(zoom, 0.1f, 64f);

            Vector2 offsetToMouse = (localMouse - pan) / prevZoom;
            pan = localMouse - (offsetToMouse * zoom);
        }

        if (ImGui.IsWindowHovered() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            pan += io.MouseDelta;
        }

        drawList.AddImage(
            textureId,
            windowPos + pan,
            windowPos + pan + (textureSize * zoom),
            new Vector2(0, 0),
            new Vector2(1, 1)
        );

        ImGui.EndChild();

        // Save updated state
        ImageViewStates[name] = new ImageViewState(zoom, pan);
    }
}