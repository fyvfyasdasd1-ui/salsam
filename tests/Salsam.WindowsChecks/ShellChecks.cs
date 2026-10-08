using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Salsam.App;
using Salsam.Core;

public static class ShellChecks
{
    private static readonly string[] Targets =
    [
        "show-extensions", "show-hidden-files", "info-tips", "thumbnails",
        "selection-checkboxes", "status-bar", "separate-process", "compressed-color"
    ];

    public static void Run()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            throw new PlatformNotSupportedException("Проверки Проводника требуют Windows 10/11.");
        var nativeType = typeof(ShellSettingsService).GetNestedType("ShellState", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Не найдено описание SHELLSTATE.");
        Check(Marshal.SizeOf(nativeType) == 32, "SHELLSTATE has the SDK's 32-byte layout");
        Check(Marshal.OffsetOf(nativeType, "Flags2").ToInt32() == 28
            && Marshal.OffsetOf(nativeType, "SortColumn").ToInt32() == 12,
            "SHELLSTATE preserves 32-bit LONG and final bitfield offsets on x64");

        var backend = new WindowsBackend();
        var originals = Snapshot(backend);
        foreach (var target in Targets)
            Check(originals[target] is "true" or "false", target + ": native Shell preference is readable");
        var directory = Path.Combine(Path.GetTempPath(), "Salsam-shell-check-" + Guid.NewGuid());
        var journal = new ChangeJournal(directory);
        var service = new ChangeService(journal, backend);
        var completed = false;
        try
        {
            // Both native bitfield groups and a nonadjacent first-group flag.
            foreach (var target in new[] { "show-extensions", "selection-checkboxes", "info-tips" })
            {
                var opposite = originals[target] == "true" ? "false" : "true";
                var record = service.Apply("shell", target, opposite);
                var after = Snapshot(backend);
                Check(after[target] == opposite, target + ": changed preference verified");
                Check(Targets.Where(t => t != target).All(t => after[t] == originals[t]),
                    target + ": unrelated Shell preferences preserved");
                Check(record.Before == originals[target] && journal.Read().Last().Before == originals[target],
                    target + ": original state persisted before application");
                service.Restore(record);
                Check(Targets.All(t => backend.Read("shell", t) == originals[t]),
                    target + ": exact original Shell state restored");
                Check(journal.Read().Last().Status == "Восстановлено", target + ": restoration journal verified");
            }
            completed = true;
        }
        finally
        {
            // Apply journals its original value before the native write. Attempt
            // every remaining restoration even if one check or rollback fails.
            var errors = new List<Exception>();
            foreach (var record in journal.Read().Reverse().Where(r => r.Status != "Восстановлено"))
            {
                try { service.Restore(record); }
                catch (Exception ex) { errors.Add(ex); }
            }
            try
            {
                Check(Targets.All(t => backend.Read("shell", t) == originals[t]),
                    "Shell check cleanup verifies every original preference");
            }
            catch (Exception ex) { errors.Add(ex); }
            if (errors.Count > 0)
                throw new AggregateException("Не удалось полностью восстановить настройки Проводника. Журнал сохранён: " + directory, errors);
            if (completed) Directory.Delete(directory, true);
        }
    }

    private static Dictionary<string, string> Snapshot(WindowsBackend backend) =>
        Targets.ToDictionary(target => target, target => backend.Read("shell", target));

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine("PASS " + name);
    }
}
