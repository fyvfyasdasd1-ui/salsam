using System.Runtime.InteropServices;

namespace Salsam.App;

public record PerformanceSnapshot(double? GpuPercent, double? DiskReadMb, double? DiskWriteMb,
    string GpuStatus, string DiskStatus);

/// <summary>
/// Reads Windows PDH rate counters. GPU is the busiest individual 3D counter instance,
/// not a sum of overlapping engines or a claim about total adapter utilization.
/// </summary>
public sealed class PerformanceService : IDisposable
{
    private const uint Success = 0, NewData = 1, MoreData = 0x800007D2;
    private const uint FormatDouble = 0x200, FormatNoCap100 = 0x8000;
    private const uint MaxArrayBytes = 16 * 1024 * 1024;
    private readonly object gate = new();
    private readonly CounterQuery gpu;
    private readonly CounterQuery disk;
    private bool disposed;

    public PerformanceService()
    {
        gpu = new CounterQuery(@"\GPU Engine(*)\Utilization Percentage");
        disk = new CounterQuery(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec",
            @"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
    }

    public PerformanceSnapshot Sample()
    {
        lock (gate)
        {
            if (disposed) return new(null, null, null, "Мониторинг остановлен.", "Мониторинг остановлен.");
            var gpuReady = gpu.Collect(out var gpuStatus);
            double? gpuValue = null;
            if (gpuReady)
            {
                var measurement = ReadGpu(gpu.Handles[0]);
                gpuValue = measurement.Value;
                gpuStatus = measurement.Status;
            }

            double? read = null, write = null;
            if (disk.Collect(out var diskStatus))
            {
                var readMeasurement = ReadRate(disk.Handles[0], disk.Errors[0]);
                var writeMeasurement = ReadRate(disk.Handles[1], disk.Errors[1]);
                read = readMeasurement.Value;
                write = writeMeasurement.Value;
                diskStatus = read.HasValue && write.HasValue
                    ? "Все физические диски: чтение и запись, МиБ/с."
                    : $"Чтение: {readMeasurement.Status} Запись: {writeMeasurement.Status}";
            }
            return new(gpuValue, read, write, gpuStatus, diskStatus);
        }
    }

    private static (double? Value, string Status) ReadRate(IntPtr counter, string? counterError)
    {
        if (counter == IntPtr.Zero) return (null, counterError ?? "Счётчик недоступен.");
        var result = PdhGetFormattedCounterValue(counter, FormatDouble | FormatNoCap100, out _, out var value);
        if (result != Success) return (null, Error(result));
        if (!Valid(value)) return (null, "Счётчик ещё не предоставил корректный интервал измерения.");
        return (value.Value / 1048576.0, "Измерение доступно.");
    }

    private static (double? Value, string Status) ReadGpu(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return (null, "Счётчик GPU недоступен.");
        // Instances may appear between sizing and reading; retry only buffer growth.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint bytes = 0;
            var result = PdhGetFormattedCounterArray(counter, FormatDouble | FormatNoCap100,
                ref bytes, out var count, IntPtr.Zero);
            if (result != MoreData && result != Success) return (null, Error(result));
            // Only the buffer size is reliable on the initial sizing call.
            if (bytes == 0) return (null, "Нет доступных экземпляров счётчика GPU.");
            if (bytes > MaxArrayBytes) return (null, "Массив счётчиков GPU превышает допустимый размер.");
            var buffer = Marshal.AllocHGlobal(checked((int)bytes));
            try
            {
                var capacity = bytes;
                result = PdhGetFormattedCounterArray(counter, FormatDouble | FormatNoCap100,
                    ref bytes, out count, buffer);
                if (result == MoreData) continue;
                if (result != Success) return (null, Error(result));
                if (count == 0) return (null, "Нет доступных экземпляров счётчика GPU.");
                var itemSize = Marshal.SizeOf<FormattedCounterItem>();
                if (count > capacity / itemSize) return (null, "Некорректный размер массива счётчиков GPU.");
                double? maximum = null;
                for (uint index = 0; index < count; index++)
                {
                    var item = Marshal.PtrToStructure<FormattedCounterItem>(
                        IntPtr.Add(buffer, checked((int)index * itemSize)));
                    var name = item.Name == IntPtr.Zero ? null : Marshal.PtrToStringUni(item.Name);
                    if (name?.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase) != true || !Valid(item.Value)) continue;
                    maximum = Math.Max(maximum ?? 0, Math.Clamp(item.Value.Value, 0, 100));
                }
                return maximum.HasValue
                    ? (maximum, "Максимум отдельного 3D-движка/процесса; не общая загрузка видеокарты.")
                    : (null, "Нет корректных измерений 3D-движков; драйвер или активное приложение их не предоставляют.");
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return (null, "Экземпляры счётчика GPU изменились во время чтения; ожидается следующий замер.");
    }

    private static bool Valid(FormattedCounterValue value) =>
        value.Status is Success or NewData && double.IsFinite(value.Value) && value.Value >= 0;

    private static string Error(uint code) => code switch
    {
        0x800007D1 => "Экземпляр счётчика отсутствует.",
        0x800007D5 => "Счётчик не предоставил данные; ожидается следующий замер.",
        0xC0000BB8 => "Объект счётчика недоступен в этой системе.",
        0xC0000BB9 => "Счётчик отсутствует в этой системе или драйвере.",
        0xC0000BC6 => "Данные счётчика пока недействительны.",
        0xC0000BDB => "Нет доступа к счётчику производительности.",
        _ => $"Счётчик недоступен (PDH 0x{code:X8})."
    };

    public static string PowerStatus()
    {
        if (!OperatingSystem.IsWindows()) return "Источник питания доступен только в Windows.";
        if (!GetSystemPowerStatus(out var power)) return "Windows не предоставила сведения об источнике питания.";
        var source = power.AcLineStatus switch { 0 => "От батареи", 1 => "От сети", _ => "Источник питания неизвестен" };
        if (power.BatteryFlag != 255 && (power.BatteryFlag & 128) != 0) return source + " · аккумулятор отсутствует";
        var charge = power.BatteryLifePercent <= 100 ? $"батарея {power.BatteryLifePercent}%" : "заряд батареи неизвестен";
        var charging = power.BatteryFlag != 255 && (power.BatteryFlag & 8) != 0 ? " · заряжается" : "";
        var saver = power.SystemStatusFlag == 1 ? " · экономия заряда включена" : "";
        return $"{source} · {charge}{charging}{saver}";
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            gpu.Dispose();
            disk.Dispose();
        }
    }

    private sealed class CounterQuery : IDisposable
    {
        private IntPtr query;
        private bool warmed;
        private readonly string? initializationError;
        public IntPtr[] Handles { get; }
        public string?[] Errors { get; }

        public CounterQuery(params string[] paths)
        {
            Handles = new IntPtr[paths.Length];
            Errors = new string?[paths.Length];
            if (!OperatingSystem.IsWindows())
            {
                initializationError = "Счётчики производительности доступны только в Windows.";
                return;
            }
            try
            {
                var result = PdhOpenQuery(null, UIntPtr.Zero, out query);
                if (result != Success) { initializationError = Error(result); query = IntPtr.Zero; return; }
                for (var index = 0; index < paths.Length; index++)
                {
                    result = PdhAddEnglishCounter(query, paths[index], UIntPtr.Zero, out var counter);
                    if (result == Success) Handles[index] = counter;
                    else Errors[index] = Error(result);
                }
                if (Handles.All(handle => handle == IntPtr.Zero))
                {
                    initializationError = string.Join(" ", Errors.Where(error => error != null).Distinct());
                    Dispose();
                }
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
            {
                initializationError = "Windows PDH недоступен в этой системе.";
                Dispose();
            }
        }

        public bool Collect(out string status)
        {
            if (query == IntPtr.Zero)
            {
                status = initializationError ?? "Счётчик недоступен.";
                return false;
            }
            var result = PdhCollectQueryData(query);
            if (result != Success) { status = Error(result); return false; }
            if (!warmed)
            {
                warmed = true;
                status = "Первый замер: ожидание следующего интервала измерения.";
                return false;
            }
            status = "Измерение доступно.";
            return true;
        }

        public void Dispose()
        {
            if (query == IntPtr.Zero) return;
            PdhCloseQuery(query);
            query = IntPtr.Zero;
            Array.Clear(Handles);
        }
    }

    // PDH_FMT_COUNTERVALUE contains a DWORD followed by an 8-byte-aligned union.
    // Nesting this structure preserves native alignment for the item array on x86/x64.
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct FormattedCounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Value;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct FormattedCounterItem
    {
        public IntPtr Name;
        public FormattedCounterValue Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, UIntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, UIntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out FormattedCounterValue value);
    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint count, IntPtr buffer);
    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
