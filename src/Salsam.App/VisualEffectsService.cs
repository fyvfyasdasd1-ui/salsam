using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Salsam.App;

/// <summary>
/// Documented, per-user Windows animation preferences. Reads and writes use
/// SystemParametersInfo, so Windows persists and broadcasts each change.
/// </summary>
public static class VisualEffectsService
{
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

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadBoolean(uint action, uint parameter, ref int value, uint flags);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteBoolean(uint action, uint parameter, IntPtr value, uint flags);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
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
            throw new ArgumentException("Для настройки анимации допустимы только true или false.", nameof(value));
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
        // Boolean SET actions take a BOOL value in pvParam, not a pointer to a BOOL.
        EnsureSuccess(WriteBoolean(actions.Write, 0, new IntPtr(enabled ? 1 : 0), UpdateProfile | BroadcastChange), target);
    }

    private static (uint Read, uint Write) Actions(string target) => target switch
    {
        "menu-animation" => (0x1002, 0x1003), // SPI_GET/SETMENUANIMATION
        "tooltip-animation" => (0x1016, 0x1017), // SPI_GET/SETTOOLTIPANIMATION
        "client-area-animation" => (0x1042, 0x1043), // SPI_GET/SETCLIENTAREAANIMATION
        _ => throw new NotSupportedException($"Неизвестная настройка анимации: {target}.")
    };

    private static AnimationInfo NewAnimationInfo() => new() { Size = (uint)Marshal.SizeOf<AnimationInfo>() };
    private static string Text(bool enabled) => enabled ? "true" : "false";
    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            throw new PlatformNotSupportedException("Настройки анимации доступны в Windows 10/11.");
    }
    private static void EnsureSuccess(bool success, string target)
    {
        if (!success)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Windows не удалось прочитать или изменить настройку «{target}».");
    }
}
