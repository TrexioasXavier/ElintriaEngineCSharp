using ElintriaEngine;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

var native = new NativeWindowSettings
{
    Title = "Elintria Engine",
    ClientSize = (1600, 900),
    APIVersion = new Version(3, 3),
    Profile = ContextProfile.Core,
};
using var win = new EditorWindow(GameWindowSettings.Default, native);
win.Run();