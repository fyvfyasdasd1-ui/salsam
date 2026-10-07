using System.IO;
using Salsam.App;
using Salsam.Core;

public static class VisualChecks
{
    public static void Run()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Нужна Windows 10/11.");
        var backend = new WindowsBackend();
        string[] targets = ["menu-animation", "tooltip-animation", "client-area-animation", "minimize-animation"];
        foreach (var target in targets)
        {
            Check(backend.Read("visual", target) is "true" or "false", $"{target}: valid Windows animation preference");
        }

        const string testTarget = "menu-animation";
        var directory = Path.Combine(Path.GetTempPath(), "Salsam-visual-check-" + Guid.NewGuid());
        var journal = new ChangeJournal(directory);
        var service = new ChangeService(journal, backend);
        var original = backend.Read("visual", testTarget);
        var opposite = original == "true" ? "false" : "true";
        var completed = false;
        try
        {
            var record = service.Apply("visual", testTarget, opposite);
            Check(backend.Read("visual", testTarget) == opposite, "menu animation: changed preference verified");
            Check(record.Before == original && journal.Read().Single().Before == original, "menu animation: original preference persisted");
            service.Restore(record);
            Check(backend.Read("visual", testTarget) == original, "menu animation: original preference restored");
            Check(journal.Read().Single().Status == "Восстановлено", "menu animation: restoration journal verified");
            completed = true;
        }
        finally
        {
            // Apply also persists a record before the native call. Restore it even
            // when an application/verification check throws after changing Windows.
            var record = journal.Read().LastOrDefault();
            if (record is not null && record.Status != "Восстановлено") service.Restore(record);
            if (backend.Read("visual", testTarget) != original)
                throw new IOException("Не удалось восстановить настройку анимации меню. Сохранён журнал: " + directory);
            if (completed) Directory.Delete(directory, true);
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine("PASS " + name);
    }
}
