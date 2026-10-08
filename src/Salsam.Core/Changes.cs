using System.Text.Json;

namespace Salsam.Core;

public record ChangeRecord(Guid Id, DateTimeOffset Created, string Kind, string Target,
    string Before, string After, string Status, string? Error = null, string? ActualAfter = null);

public sealed class ChangeJournal
{
    private readonly string path;
    public ChangeJournal(string directory) { Directory.CreateDirectory(directory); path = Path.Combine(directory, "changes.json"); }
    public IReadOnlyList<ChangeRecord> Read() => File.Exists(path)
        ? JsonSerializer.Deserialize<List<ChangeRecord>>(File.ReadAllText(path)) ?? [] : [];
    public void Save(ChangeRecord record)
    {
        var records = Read().ToList();
        var index = records.FindIndex(r => r.Id == record.Id);
        if (index < 0) records.Add(record); else records[index] = record;
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}

public interface ISettingBackend
{
    string Read(string kind, string target);
    void Write(string kind, string target, string value);
}

public interface ISettingValueComparer
{
    bool Matches(string kind, string target, string actual, string desired);
}

public sealed record ApplyOutcome(ChangeRecord? Record, bool AlreadyConfigured, string CurrentValue);

public sealed class ChangeService(ChangeJournal journal, ISettingBackend backend)
{
    public ChangeRecord Apply(string kind, string target, string after)
    {
        var before = backend.Read(kind, target);
        return ApplyCaptured(kind, target, after, before);
    }

    public ApplyOutcome ApplyIfNeeded(string kind, string target, string after)
    {
        // A queued card is only a suggestion: reread immediately before deciding whether to write.
        var before = backend.Read(kind, target);
        if (Matches(kind, target, before, after)) return new(null, true, before);
        var record = ApplyCaptured(kind, target, after, before);
        return new(record, false, record.ActualAfter ?? record.After);
    }

    private ChangeRecord ApplyCaptured(string kind, string target, string after, string before)
    {
        var record = new ChangeRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, kind, target, before, after, "Подготовлено");
        journal.Save(record); // Persist recovery data before touching the setting.
        try
        {
            backend.Write(kind, target, after);
            var observed = backend.Read(kind, target);
            record = record with { ActualAfter = observed };
            if (!Matches(kind, target, observed, after)) throw new IOException("Проверка применения не прошла.");
            record = record with { Status = "Применено" };
            journal.Save(record);
            return record;
        }
        catch (Exception ex)
        {
            if (record.ActualAfter is null)
            {
                try { record = record with { ActualAfter = backend.Read(kind, target) }; }
                catch { /* Keep the original error and recovery snapshot when observation is unavailable. */ }
            }
            journal.Save(record with { Status = "Ошибка — проверьте состояние и выполните откат", Error = ex.Message });
            throw;
        }
    }
    public void Restore(ChangeRecord record)
    {
        if (record.Status == "Восстановлено") throw new InvalidOperationException("Изменение уже восстановлено.");
        var current = backend.Read(record.Kind, record.Target);
        var expectedAfter = record.ActualAfter ?? record.After;
        if (!string.Equals(current, expectedAfter, StringComparison.Ordinal) &&
            !string.Equals(current, record.Before, StringComparison.Ordinal))
            throw new InvalidOperationException("Состояние изменено вне приложения. Автоматический откат отменён.");
        if (!string.Equals(current, record.Before, StringComparison.Ordinal))
            backend.Write(record.Kind, record.Target, record.Before);
        if (!string.Equals(backend.Read(record.Kind, record.Target), record.Before, StringComparison.Ordinal))
            throw new IOException("Проверка отката не прошла.");
        journal.Save(record with { Status = "Восстановлено", Error = null });
    }

    private bool Matches(string kind, string target, string actual, string desired) =>
        backend is ISettingValueComparer comparer
            ? comparer.Matches(kind, target, actual, desired)
            : string.Equals(actual, desired, StringComparison.Ordinal);
}
