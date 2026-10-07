using Salsam.App;

public static class PerformanceChecks
{
    /// <summary>Reads counters only; no system settings are changed.</summary>
    public static void Run()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Эти проверки требуют Windows 10/11.");
        using var service = new PerformanceService();
        var first = service.Sample();
        Check(first.GpuPercent == null && first.DiskReadMb == null && first.DiskWriteMb == null,
            "PDH first measurement is warmup or explicitly unavailable");
        Check(!string.IsNullOrWhiteSpace(first.GpuStatus) && !string.IsNullOrWhiteSpace(first.DiskStatus),
            "warmup and unavailable counters have explanations");
        Thread.Sleep(1100);
        var sample = service.Sample();
        Validate(sample.GpuPercent, sample.GpuStatus, "GPU 3D instance", 100);
        Validate(sample.DiskReadMb, sample.DiskStatus, "disk read MiB/s");
        Validate(sample.DiskWriteMb, sample.DiskStatus, "disk write MiB/s");
        if (sample.GpuPercent.HasValue)
            Check(sample.GpuStatus.Contains("не общая", StringComparison.Ordinal), "GPU metric states its limited scope");
        var power = PerformanceService.PowerStatus();
        Check(!string.IsNullOrWhiteSpace(power) && !power.Contains("только в Windows", StringComparison.Ordinal),
            "native Windows power status has a human explanation");
        Console.WriteLine("POWER " + power);
        service.Dispose();
        var stopped = service.Sample();
        Check(stopped.GpuPercent == null && stopped.DiskReadMb == null && stopped.DiskWriteMb == null
            && stopped.GpuStatus.Contains("остановлен", StringComparison.Ordinal), "disposed PDH service releases query and stops sampling");
    }

    private static void Validate(double? value, string status, string name, double? maximum = null)
    {
        Check(!string.IsNullOrWhiteSpace(status), name + " reports availability");
        if (value.HasValue)
            Check(double.IsFinite(value.Value) && value >= 0 && (!maximum.HasValue || value <= maximum), name + " returns valid measured data");
        else Console.WriteLine("UNAVAILABLE " + name + ": " + status);
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine("PASS " + name);
    }
}
