namespace TestD3D12.Graphics;

public static class ShaderLoader
{
    public static string ResolveShaderPathFromSimpleName(string simpleName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders", "Compiled", simpleName + ".dxil");
    }

    public static ReadOnlyMemory<byte> LoadShaderBytecode(string simpleName)
    {
        return File.ReadAllBytes(ResolveShaderPathFromSimpleName(simpleName));
    }
}
