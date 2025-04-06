## MiniEngine
A small, minimal game engine written in C# using D3D12 through the [Vortice.Windows bindings](https://github.com/amerkoleci/Vortice.Windows)

Designed to be an efficient forward renderer with hybrid ray-tracing support for shadows and reflections

Depends on [ImGui.NET](https://github.com/ImGuiNET/ImGui.NET) for debug UI

## Building

Uses .NET 9.0 and runs on Windows and Linux (native support through [vkd3d](https://github.com/HansKristian-Work/vkd3d-proton) (actually a custom fork with some fixes https://github.com/chairclr/vkd3d-proton))
