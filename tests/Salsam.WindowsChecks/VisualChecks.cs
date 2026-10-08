using System.IO;
using Salsam.App;
using Salsam.Core;

public static class VisualChecks
{
    public static void Run()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Нужна Windows 10/11.");
        var backend = new WindowsBackend();
        var original = VisualEffectsService.SupportedTargets.ToDictionary(target => target, target => backend.Read("visual", target));
        foreach (var target in VisualEffectsService.SupportedTargets)
        {
            Check(original[target] is "true" or "false", $"{target}: valid Windows interface preference");
        }

        var directory = Path.Combine(Path.GetTempPath(), "Salsam-visual-check-" + Guid.NewGuid());
        var journal = new ChangeJournal(directory);
        var service = new ChangeService(journal, backend);
        var completed = false;
        try
        {
            // Exercise the original bool path, a new fade preference and the
            // distinct uiParam setter used by full-window dragging.
            foreach (var target in new[] { "menu-animation", "tooltip-fade", "full-window-drag" })
            {
                var opposite = original[target] == "true" ? "false" : "true";
                var record = service.Apply("visual", target, opposite);
                Check(backend.Read("visual", target) == opposite, $"{target}: changed preference verified");
                Check(record.Before == original[target] && journal.Read().Single(r => r.Id == record.Id).Before == original[target], $"{target}: original preference persisted");
                Check(original.Where(pair => pair.Key != target).All(pair => backend.Read("visual", pair.Key) == pair.Value), $"{target}: unrelated preferences preserved");
                service.Restore(record);
                Check(backend.Read("visual", target) == original[target], $"{target}: original preference restored");
                Check(journal.Read().Single(r => r.Id == record.Id).Status == "Восстановлено", $"{target}: restoration journal verified");
            }
            completed = true;
        }
        finally
        {
            // Apply also persists a record before the native call. Restore it even
            // when an application/verification check throws after changing Windows.
            foreach (var record in journal.Read().Reverse().Where(r => r.Status != "Восстановлено")) service.Restore(record);
            if (original.Any(pair => backend.Read("visual", pair.Key) != pair.Value))
                throw new IOException("Не удалось восстановить настройки интерфейса. Сохранён журнал: " + directory);
            if (completed) Directory.Delete(directory, true);
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine("PASS " + name);
    }
}
