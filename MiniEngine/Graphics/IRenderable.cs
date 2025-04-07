using Vortice.Direct3D12;

namespace MiniEngine.Graphics;

public interface IRenderable
{
    void Render(ID3D12GraphicsCommandList4 commandList);

    void RenderDepth(ID3D12GraphicsCommandList4 commandList);
}