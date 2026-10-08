using Salsam.Core;

var directory = Path.Combine(Path.GetTempPath(), "salsam-tests-" + Guid.NewGuid());
var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
void Throws(Action action, string name) { try { action(); } catch { Check(true, name); return; } throw new Exception("Expected failure: " + name); }
try
{
    var journal = new ChangeJournal(directory);
    var backend = new FakeBackend(journal);
    var service = new ChangeService(journal, backend);
    var record = service.Apply("power", "active", "new");
    Check(backend.SavedBeforeWrite && record.Before == "old" && journal.Read()[0].Status == "Применено", "persist original before write and verify apply");
    service.Restore(record);
    Check(backend.Value == "old" && journal.Read()[0].Status == "Восстановлено", "restore original and persist verified result");
    record = service.Apply("power", "active", "new");
    backend.Value = "external";
    Throws(() => service.Restore(record), "external modification blocks rollback");
    Check(backend.Value == "external", "conflict does not overwrite external state");
    backend.Value = "old"; backend.Fail = true;
    Throws(() => service.Apply("power", "active", "new"), "write failure surfaces");
    Check(journal.Read().Last().Before == "old" && journal.Read().Last().Error != null, "write failure retains recovery data");
    backend.Fail = false; backend.IgnoreWrite = true;
    Throws(() => service.Apply("power", "active", "new"), "read-back detects ineffective write");
    backend.IgnoreWrite = false;
    service.Restore(journal.Read().Last());
    Check(backend.Value == "old", "failed application can be recovered");
    var csv = "FrameTimeMs\n" + string.Join('\n', Enumerable.Repeat("10", 99).Append("100"));
    var report = FrameReport.Parse(csv);
    Check(report.Count == 100 && Math.Abs(report.AverageFps - 1000 / 10.9) < .001 && report.OnePercentLow == 10, "average FPS and slowest one percent use frame time");
    Throws(() => FrameReport.Parse("FrameTimeMs\n0"), "reject invalid frame time");
    Throws(() => FrameReport.Parse("FrameTimeMs\nNaN"), "reject nonfinite frame time");
    Throws(() => FrameReport.Parse("FrameTimeMs\n10"), "reject insufficient sample");
    Throws(() => FrameReport.Parse("FPS\n100"), "reject unsupported measurement column");
    Check(new ChangeJournal(directory).Read().Count == 4, "journal survives reopening");
    passed += ProfileChecks.Run();
    passed += BenchmarkChecks.Run();
    passed += StateChecks.Run();
    Console.WriteLine($"{passed} checks passed.");
}
finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

sealed class FakeBackend(ChangeJournal journal) : ISettingBackend
{
    public string Value = "old";
    public bool Fail, IgnoreWrite, SavedBeforeWrite;
    public string Read(string kind, string target) => Value;
    public void Write(string kind, string target, string value)
    {
        SavedBeforeWrite = journal.Read().Count > 0;
        if (Fail) throw new IOException("simulated failure");
        if (!IgnoreWrite) Value = value;
    }
}
