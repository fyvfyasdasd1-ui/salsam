using System.Globalization;
using System.IO;
using System.Text.Json;
using Salsam.App;
using Salsam.Core;

public static class InputChecks
{
    public static void Run()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Нужна Windows 10/11.");
        var backend = new WindowsBackend();
        var original = InputSettingsService.SupportedTargets.ToDictionary(target => target, target => backend.Read("input", target));
        var originalVisual = VisualEffectsService.SupportedTargets.ToDictionary(target => target, target => backend.Read("visual", target));
        var mouse = JsonSerializer.Deserialize<int[]>(original["mouse-acceleration"])!;
        Check(mouse is { Length: 3 } && mouse[0] >= 0 && mouse[1] >= 0 && mouse[2] is >= 0 and <= 2, "mouse acceleration: three native integer preferences");
        Check(IsInteger(original["mouse-trails"], 0, 16), "mouse trails: native supported length including disabled aliases");
        Check(IsInteger(original["keyboard-delay"], 0, 3), "keyboard delay: native supported range");
        Check(IsInteger(original["keyboard-speed"], 0, 31), "keyboard speed: native supported range");

        var desired = new Dictionary<string, string>
        {
            ["mouse-acceleration"] = mouse[2] == 0 ? "[0,0,1]" : "[0,0,0]",
            ["mouse-trails"] = original["mouse-trails"] is "0" or "1" ? "2" : "0",
            ["keyboard-delay"] = original["keyboard-delay"] == "0" ? "1" : "0",
            ["keyboard-speed"] = original["keyboard-speed"] == "31" ? "30" : "31"
        };
        var directory = Path.Combine(Path.GetTempPath(), "Salsam-input-check-" + Guid.NewGuid());
        var journal = new ChangeJournal(directory);
        var service = new ChangeService(journal, backend);
        var completed = false;
        try
        {
            foreach (var target in InputSettingsService.SupportedTargets)
            {
                var outcome = service.ApplyIfNeeded("input", target, desired[target]);
                Check(!outcome.AlreadyConfigured && outcome.Record != null, $"{target}: changed preference returns a journal record");
                var record = outcome.Record!;
                var actual = backend.Read("input", target);
                var changedMouse = target == "mouse-acceleration" ? JsonSerializer.Deserialize<int[]>(actual)! : null;
                Check(backend.Matches("input", target, actual, desired[target])
                    && (changedMouse == null || changedMouse[0] == mouse[0] && changedMouse[1] == mouse[1]),
                    $"{target}: desired preference verified; mouse thresholds preserved");
                Check(record.Before == original[target] && record.ActualAfter == actual, $"{target}: exact native before/after snapshot persisted");
                var savedRecords = journal.Read().ToArray();
                var repeated = service.ApplyIfNeeded("input", target, desired[target]);
                Check(repeated.AlreadyConfigured && repeated.Record == null && repeated.CurrentValue == actual, $"{target}: repeat application is already configured");
                Check(journal.Read().SequenceEqual(savedRecords), $"{target}: repeat application leaves journal unchanged");
                Check(original.Where(pair => pair.Key != target).All(pair => backend.Read("input", pair.Key) == pair.Value)
                    && originalVisual.All(pair => backend.Read("visual", pair.Key) == pair.Value), $"{target}: unrelated input and interface preferences preserved");
                service.Restore(record);
                Check(backend.Read("input", target) == original[target], $"{target}: exact original preference restored");
                Check(journal.Read().Single(r => r.Id == record.Id).Status == "Восстановлено", $"{target}: restoration journal verified");
            }
            completed = true;
        }
        finally
        {
            // Includes recovery for a native write followed by a failed check.
            foreach (var record in journal.Read().Reverse().Where(r => r.Status != "Восстановлено")) service.Restore(record);
            if (original.Any(pair => backend.Read("input", pair.Key) != pair.Value)
                || originalVisual.Any(pair => backend.Read("visual", pair.Key) != pair.Value))
                throw new IOException("Проверка обнаружила изменённые исходные настройки. Сохранён журнал: " + directory);
            if (completed) Directory.Delete(directory, true);
        }
    }

    private static bool IsInteger(string text, int minimum, int maximum) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && value >= minimum && value <= maximum && value.ToString(CultureInfo.InvariantCulture) == text;

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine("PASS " + name);
    }
}
