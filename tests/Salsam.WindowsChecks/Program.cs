using System.IO;
using Microsoft.Win32;
using Salsam.App;
using Salsam.Core;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Нужна Windows 10/11.");
var id = "Salsam-check-" + Guid.NewGuid();
var directory = Path.Combine(Path.GetTempPath(), id + "-journal");
var temporary = Path.Combine(Path.GetTempPath(), id + ".tmp");
var backend = new WindowsBackend();
var service = new ChangeService(new ChangeJournal(directory), backend);
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); }
try
{
    using (var key = Registry.CurrentUser.CreateSubKey(WindowsBackend.RunKey))
        key.SetValue(id, "C:\\Salsam-nonexistent-test.exe", RegistryValueKind.ExpandString);
    var original = backend.Read("startup", id);
    var startup = service.Apply("startup", id, "null");
    Check(backend.Read("startup", id) == "null", "HKCU Run disable verified");
    service.Restore(startup);
    Check(backend.Read("startup", id) == original, "HKCU Run string and type restored");
    File.WriteAllText(temporary, "Salsam integration fixture");
    File.SetLastWriteTimeUtc(temporary, DateTime.UtcNow.AddDays(-8));
    Check(WindowsBackend.PreviewTemps().Any(t => t.Path == temporary), "old temporary file appears in preview");
    var temp = service.Apply("temp", temporary, "quarantine");
    Check(!File.Exists(temporary) && backend.Read("temp", temporary) == "quarantine", "quarantine move verified");
    service.Restore(temp);
    Check(File.ReadAllText(temporary) == "Salsam integration fixture", "quarantine restores bytes");
    Check(WindowsBackend.Plans().Any(p => p.Id == backend.Read("power", "active")), "active power scheme found");
    var monitor = new MonitorService(); monitor.Sample(); Thread.Sleep(200);
    var sample = monitor.Sample();
    Check(sample.Cpu is >= 0 and <= 100 && sample.TotalGb > 0, "CPU and RAM native counters");
    VisualChecks.Run();
    InputChecks.Run();
    ShellChecks.Run();
    PowerChecks.Run();
    QueueStateChecks.Run();
    PerformanceChecks.Run();
    Console.WriteLine("Windows integration checks completed (optional counters report availability separately).");
}
finally
{
    using var key = Registry.CurrentUser.OpenSubKey(WindowsBackend.RunKey, true);
    key?.DeleteValue(id, false);
    if (backend.Read("temp", temporary) == "quarantine") backend.Write("temp", temporary, "original");
    if (File.Exists(temporary)) File.Delete(temporary);
    if (Directory.Exists(directory)) Directory.Delete(directory, true);
}
