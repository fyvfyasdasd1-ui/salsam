using System.IO;
using System.Runtime.InteropServices;

namespace Salsam.App;

/// <summary>
/// Documented per-user Explorer preferences. SHGetSetSettings applies only the
/// requested SSF mask; it does not reset unrelated Shell preferences.
/// </summary>
public static class ShellSettingsService
{
    // Source of the native layout and SSF masks:
    // https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/ShlObj_core.h
    // Semantics: https://learn.microsoft.com/windows/win32/api/shlobj_core/ns-shlobj_core-shellstatea
    // API: https://learn.microsoft.com/windows/win32/api/shlobj_core/nf-shlobj_core-shgetsetsettings
    // Windows LONG remains 32 bits on x64. SHELLSTATE has no pointer-sized fields,
    // so the SDK layout is exactly 32 bytes on both x86 and x64.
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct ShellState
    {
        [FieldOffset(0)] public uint Flags;
        [FieldOffset(4)] public uint Win95Unused;
        [FieldOffset(8)] public uint Win95UnusedLength;
        [FieldOffset(12)] public int SortColumn;
        [FieldOffset(16)] public int SortDirection;
        [FieldOffset(20)] public uint Version;
        [FieldOffset(24)] public uint Unused;
        [FieldOffset(28)] public uint Flags2;
    }

    private readonly record struct Preference(uint Mask, uint Bit, bool SecondGroup = false, bool Inverted = false);

    [DllImport("shell32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void SHGetSetSettings(ref ShellState state, uint mask, [MarshalAs(UnmanagedType.Bool)] bool set);

    public static string Read(string target)
    {
        var preference = Describe(target);
        RequireWindows();
        var state = ReadState(preference.Mask);
        return Enabled(state, preference) ? "true" : "false";
    }

    public static void Write(string target, string value)
    {
        if (value is not ("true" or "false"))
            throw new ArgumentException("Для настройки Проводника допустимы только true или false.", nameof(value));
        var preference = Describe(target);
        RequireWindows();
        var state = ReadState(preference.Mask);
        var bitEnabled = (value == "true") != preference.Inverted;
        if (preference.SecondGroup)
            state.Flags2 = bitEnabled ? state.Flags2 | preference.Bit : state.Flags2 & ~preference.Bit;
        else
            state.Flags = bitEnabled ? state.Flags | preference.Bit : state.Flags & ~preference.Bit;

        // No broad mask, registry edits or Explorer restart: only this preference
        // is persisted by the Shell. Existing folders may need to be reopened.
        Invoke(ref state, preference.Mask, true);
        // SHGetSetSettings returns void and does not define GetLastError. A fresh
        // readback detects ignored writes, including restrictions imposed by policy.
        if (Read(target) != value)
            throw new IOException($"Windows не подтвердила изменение настройки Проводника «{target}». Настройка может быть ограничена политикой.");
    }

    private static Preference Describe(string target) => target switch
    {
        "show-extensions" => new(0x00000002, 1u << 1), // SSF_SHOWEXTENSIONS / fShowExtensions
        "show-hidden-files" => new(0x00000001, 1u << 0), // SSF_SHOWALLOBJECTS / fShowAllObjects
        "info-tips" => new(0x00002000, 1u << 11), // SSF_SHOWINFOTIP / fShowInfoTip
        "compressed-color" => new(0x00000008, 1u << 4), // SSF_SHOWCOMPCOLOR / fShowCompColor
        "separate-process" => new(0x00080000, 1u << 0, true), // SSF_SEPPROCESS / fSepProcess
        "selection-checkboxes" => new(0x00800000, 1u << 3, true), // SSF_AUTOCHECKSELECT / fAutoCheckSelect
        "thumbnails" => new(0x01000000, 1u << 4, true, true), // SSF_ICONSONLY / fIconsOnly, logical inversion
        "status-bar" => new(0x04000000, 1u << 6, true), // SSF_SHOWSTATUSBAR / fShowStatusBar
        _ => throw new NotSupportedException($"Неизвестная настройка Проводника: {target}.")
    };

    private static ShellState ReadState(uint mask)
    {
        // Fresh zero-initialized native structure; the API fills the masked bits.
        // SHELLSTATE.version is documented as unused, so no version is invented.
        var state = new ShellState();
        Invoke(ref state, mask, false);
        return state;
    }

    private static bool Enabled(ShellState state, Preference preference)
    {
        var flags = preference.SecondGroup ? state.Flags2 : state.Flags;
        return ((flags & preference.Bit) != 0) != preference.Inverted;
    }

    private static void Invoke(ref ShellState state, uint mask, bool set)
    {
        try { SHGetSetSettings(ref state, mask, set); }
        catch (EntryPointNotFoundException ex)
        {
            throw new PlatformNotSupportedException("Эта версия Windows не предоставляет API настройки Проводника SHGetSetSettings.", ex);
        }
        catch (DllNotFoundException ex)
        {
            throw new PlatformNotSupportedException("Системная библиотека Проводника недоступна.", ex);
        }
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            throw new PlatformNotSupportedException("Настройки Проводника доступны в Windows 10/11.");
    }
}
