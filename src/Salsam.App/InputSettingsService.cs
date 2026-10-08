using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Salsam.App;

/// <summary>
/// Documented Windows mouse and keyboard preferences. These are input preferences,
/// not measurements or promises of lower game latency.
/// </summary>
public static class InputSettingsService
{
    private const uint UpdateProfile = 0x0001;
    private const uint BroadcastChange = 0x0002;
    private const uint GetMouse = 0x0003;
    private const uint SetMouse = 0x0004;
    public static IReadOnlyList<string> SupportedTargets { get; } = Array.AsReadOnly<string>([
        "mouse-acceleration", "mouse-trails", "keyboard-delay", "keyboard-speed"
    ]);

    // SPI_GETMOUSE/SPI_SETMOUSE require three consecutive native int values.
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseParameters
    {
        public int FirstThreshold;
        public int SecondThreshold;
        public int Acceleration;
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Mouse(uint action, uint parameter, ref MouseParameters value, uint flags);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadInteger(uint action, uint parameter, ref int value, uint flags);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteInteger(uint action, uint parameter, IntPtr value, uint flags);

    public static string Read(string target)
    {
        RequireWindows();
        if (target == "mouse-acceleration")
        {
            var value = ReadMouse();
            return JsonSerializer.Serialize(new[] { value.FirstThreshold, value.SecondThreshold, value.Acceleration });
        }

        var actions = Actions(target);
        var integer = 0;
        EnsureSuccess(ReadInteger(actions.Read, 0, ref integer, 0), target);
        // Windows documents both 0 and 1 as disabled. Preserve either native
        // value here so a journal rollback can restore the exact original.
        ValidateInteger(target, integer, actions.Maximum);
        return integer.ToString(CultureInfo.InvariantCulture);
    }

    public static void Write(string target, string value)
    {
        if (target == "mouse-acceleration")
        {
            var requested = ParseMouse(value);
            RequireWindows();
            var actual = ReadMouse();
            // Profiles use placeholder thresholds. Changing the acceleration
            // mode must not silently replace the user's threshold preferences.
            actual.Acceleration = requested[2];
            EnsureSuccess(Mouse(SetMouse, 0, ref actual, UpdateProfile | BroadcastChange), target);
            return;
        }

        var actions = Actions(target);
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var integer)
            || integer.ToString(CultureInfo.InvariantCulture) != value)
            throw new ArgumentException("Требуется целое число в обычной десятичной записи.", nameof(value));
        ValidateInteger(target, integer, actions.Maximum);
        RequireWindows();
        // Numeric setters take the number in uiParam, not a pointer in pvParam.
        EnsureSuccess(WriteInteger(actions.Write, (uint)integer, IntPtr.Zero, UpdateProfile | BroadcastChange), target);
    }

    private static MouseParameters ReadMouse()
    {
        var value = new MouseParameters();
        EnsureSuccess(Mouse(GetMouse, 0, ref value, 0), "mouse-acceleration");
        ValidateMouse([value.FirstThreshold, value.SecondThreshold, value.Acceleration]);
        return value;
    }

    private static int[] ParseMouse(string value)
    {
        int[]? parameters;
        try { parameters = JsonSerializer.Deserialize<int[]>(value); }
        catch (JsonException ex) { throw new ArgumentException("Требуется JSON-массив из трёх целых чисел.", nameof(value), ex); }
        ValidateMouse(parameters);
        return parameters!;
    }

    private static void ValidateMouse(int[]? values)
    {
        if (values is not { Length: 3 } || values[0] < 0 || values[1] < 0 || values[2] is < 0 or > 2)
            throw new ArgumentException("Параметры мыши: два неотрицательных порога и режим ускорения от 0 до 2.");
    }

    private static void ValidateInteger(string target, int value, int maximum)
    {
        if (value < 0 || value > maximum)
            throw new ArgumentOutOfRangeException(nameof(value), target == "mouse-trails"
                ? "След указателя: 0 или 1 для отключения, длина от 2 до 16 для включения."
                : $"Настройка «{target}» допускает значения от 0 до {maximum}.");
    }

    private static (uint Read, uint Write, int Maximum) Actions(string target) => target switch
    {
        "mouse-trails" => (0x005E, 0x005D, 16),
        "keyboard-delay" => (0x0016, 0x0017, 3),
        "keyboard-speed" => (0x000A, 0x000B, 31),
        _ => throw new NotSupportedException($"Неизвестная настройка ввода: {target}.")
    };

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            throw new PlatformNotSupportedException("Настройки ввода доступны в Windows 10/11.");
    }
    private static void EnsureSuccess(bool success, string target)
    {
        if (!success)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Windows не удалось прочитать или изменить настройку «{target}».");
    }
}
