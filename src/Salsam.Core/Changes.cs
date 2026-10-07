using System.Text.Json;

namespace Salsam.Core;

public record ChangeRecord(Guid Id, DateTimeOffset Created, string Kind, string Target,
    string Before, string After, string Status, string? Error = null);

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

public sealed class ChangeService(ChangeJournal journal, ISettingBackend backend)
{
    public ChangeRecord Apply(string kind, string target, string after)
    {
        var before = backend.Read(kind, target);
        var record = new ChangeRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, kind, target, before, after, "Подготовлено");
        journal.Save(record); // Persist recovery data before touching the setting.
        try
        {
            backend.Write(kind, target, after);
            if (backend.Read(kind, target) != after) throw new IOException("Проверка применения не прошла.");
            record = record with { Status = "Применено" };
            journal.Save(record);
            return record;
        }
        catch (Exception ex)
        {
            journal.Save(record with { Status = "Ошибка — проверьте состояние и выполните откат", Error = ex.Message });
            throw;
        }
    }
    public void Restore(ChangeRecord record)
    {
        if (record.Status == "Восстановлено") throw new InvalidOperationException("Изменение уже восстановлено.");
        var current = backend.Read(record.Kind, record.Target);
        if (current != record.After && current != record.Before)
            throw new InvalidOperationException("Состояние изменено вне приложения. Автоматический откат отменён.");
        backend.Write(record.Kind, record.Target, record.Before);
        if (backend.Read(record.Kind, record.Target) != record.Before) throw new IOException("Проверка отката не прошла.");
        journal.Save(record with { Status = "Восстановлено", Error = null });
    }
}
