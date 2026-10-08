using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Salsam.App;

/// <summary>
/// Documented, per-user Windows interface preferences. Reads and writes use
/// SystemParametersInfo, so Windows persists and broadcasts each change.
/// </summary>
public static class VisualEffectsService
{
    public static IReadOnlyList<string> SupportedTargets { get; } = Array.AsReadOnly<string>([
        "menu-animation", "tooltip-animation", "client-area-animation", "minimize-animation",
        "menu-fade", "tooltip-fade", "combo-animation", "listbox-smooth-scrolling", "selection-fade",
        "cursor-shadow", "drop-shadow", "full-window-drag", "mouse-vanish", "mouse-sonar",
        "mouse-clicklock", "keyboard-cues", "hot-tracking"
    ]);
    private const uint UpdateProfile = 0x0001; // SPIF_UPDATEINIFILE
    private const uint BroadcastChange = 0x0002; // SPIF_SENDCHANGE
    private const uint GetAnimation = 0x0048;
    private const uint SetAnimation = 0x0049;

    [StructLayout(LayoutKind.Sequential)]
    private struct AnimationInfo
    {
        public uint Size;
        public int MinimizeAnimation;
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadBoolean(uint action, uint parameter, ref int value, uint flags);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteBoolean(uint action, uint parameter, IntPtr value, uint flags);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Animation(uint action, uint parameter, ref AnimationInfo value, uint flags);

    public static string Read(string target)
    {
        RequireWindows();
        if (target == "minimize-animation")
        {
            var animation = NewAnimationInfo();
            EnsureSuccess(Animation(GetAnimation, animation.Size, ref animation, 0), target);
            return Text(animation.MinimizeAnimation != 0);
        }

        var actions = Actions(target);
        var value = 0;
        EnsureSuccess(ReadBoolean(actions.Read, 0, ref value, 0), target);
        return Text(value != 0);
    }

    public static void Write(string target, string value)
    {
        if (value is not ("true" or "false"))
            throw new ArgumentException("Для настройки интерфейса допустимы только true или false.", nameof(value));
        RequireWindows();
        var enabled = value == "true";
        if (target == "minimize-animation")
        {
            var animation = NewAnimationInfo();
            animation.MinimizeAnimation = enabled ? 1 : 0;
            EnsureSuccess(Animation(SetAnimation, animation.Size, ref animation, UpdateProfile | BroadcastChange), target);
            return;
        }

        var actions = Actions(target);
        // Most SET actions take a BOOL value in pvParam. SPI_SETDRAGFULLWINDOWS
        // is documented to take the BOOL in uiParam instead.
        var parameter = actions.ValueInUiParameter && enabled ? 1u : 0u;
        var pointer = actions.ValueInUiParameter ? IntPtr.Zero : new IntPtr(enabled ? 1 : 0);
        EnsureSuccess(WriteBoolean(actions.Write, parameter, pointer, UpdateProfile | BroadcastChange), target);
    }

    private static (uint Read, uint Write, bool ValueInUiParameter) Actions(string target) => target switch
    {
        "menu-animation" => (0x1002, 0x1003, false), // SPI_GET/SETMENUANIMATION
        "tooltip-animation" => (0x1016, 0x1017, false), // SPI_GET/SETTOOLTIPANIMATION
        "client-area-animation" => (0x1042, 0x1043, false), // SPI_GET/SETCLIENTAREAANIMATION
        "menu-fade" => (0x1012, 0x1013, false), // SPI_GET/SETMENUFADE
        "tooltip-fade" => (0x1018, 0x1019, false), // SPI_GET/SETTOOLTIPFADE
        "combo-animation" => (0x1004, 0x1005, false), // SPI_GET/SETCOMBOBOXANIMATION
        "listbox-smooth-scrolling" => (0x1006, 0x1007, false), // SPI_GET/SETLISTBOXSMOOTHSCROLLING
        "selection-fade" => (0x1014, 0x1015, false), // SPI_GET/SETSELECTIONFADE
        "cursor-shadow" => (0x101A, 0x101B, false), // SPI_GET/SETCURSORSHADOW
        "drop-shadow" => (0x1024, 0x1025, false), // SPI_GET/SETDROPSHADOW
        "full-window-drag" => (0x0026, 0x0025, true), // SPI_GET/SETDRAGFULLWINDOWS
        "mouse-vanish" => (0x1020, 0x1021, false), // SPI_GET/SETMOUSEVANISH
        "mouse-sonar" => (0x101C, 0x101D, false), // SPI_GET/SETMOUSESONAR
        "mouse-clicklock" => (0x101E, 0x101F, false), // SPI_GET/SETMOUSECLICKLOCK
        "keyboard-cues" => (0x100A, 0x100B, false), // SPI_GET/SETKEYBOARDCUES
        "hot-tracking" => (0x100E, 0x100F, false), // SPI_GET/SETHOTTRACKING
        _ => throw new NotSupportedException($"Неизвестная настройка интерфейса: {target}.")
    };

    private static AnimationInfo NewAnimationInfo() => new() { Size = (uint)Marshal.SizeOf<AnimationInfo>() };
    private static string Text(bool enabled) => enabled ? "true" : "false";
    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            throw new PlatformNotSupportedException("Настройки интерфейса доступны в Windows 10/11.");
    }
    private static void EnsureSuccess(bool success, string target)
    {
        if (!success)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Windows не удалось прочитать или изменить настройку «{target}».");
    }
}
