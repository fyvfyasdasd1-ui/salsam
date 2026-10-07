using System.Text.Json;

namespace Salsam.Core;

public sealed record ProfileAction(string Kind, string Target, string After, string Title);
public sealed record SavedProfile(string Name, IReadOnlyList<ProfileAction> Actions);

/// <summary>
/// Stores only reusable optimization choices. Startup commands and file paths are never accepted.
/// </summary>
public sealed class ProfileStore
{
    private const int MaximumActions = 64;
    private const long MaximumFileLength = 128 * 1024;
    private readonly string path;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly HashSet<string> VisualTargets = new(StringComparer.Ordinal)
    {
        "menu-animation", "tooltip-animation", "client-area-animation", "minimize-animation"
    };

    public ProfileStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "custom-profile.json");
    }

    public SavedProfile? Read()
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumFileLength)
            throw new InvalidDataException("Файл профиля превышает допустимый размер.");
        var profile = JsonSerializer.Deserialize<SavedProfile>(stream)
            ?? throw new InvalidDataException("Файл профиля пуст или повреждён.");
        try { return ValidateAndSnapshot(profile); }
        catch (ArgumentException ex) { throw new InvalidDataException("В профиле найдены неподдерживаемые настройки.", ex); }
    }

    public void Save(SavedProfile profile)
    {
        // Validate and detach from a mutable caller-owned collection before touching the existing file.
        var snapshot = ValidateAndSnapshot(profile);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, snapshot, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static SavedProfile ValidateAndSnapshot(SavedProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateLabel(profile.Name, 80, "Название профиля");
        if (profile.Actions is null) throw new ArgumentException("В профиле отсутствует список настроек.", nameof(profile));
        var actions = profile.Actions.ToArray();
        if (actions.Length > MaximumActions)
            throw new ArgumentException($"Профиль поддерживает не более {MaximumActions} настроек.", nameof(profile));
        var selected = new HashSet<(string Kind, string Target)>();
        foreach (var action in actions)
        {
            if (action is null) throw new ArgumentException("Профиль содержит пустую настройку.", nameof(profile));
            ValidateLabel(action.Title, 160, "Название настройки");
            var valid = action.Kind switch
            {
                "power" => action.Target == "active" && Guid.TryParseExact(action.After, "D", out var scheme) && scheme != Guid.Empty,
                "visual" => VisualTargets.Contains(action.Target) && action.After is "true" or "false",
                _ => false
            };
            if (!valid)
                throw new ArgumentException("Профиль может содержать только поддерживаемые настройки питания и анимации.", nameof(profile));
            if (!selected.Add((action.Kind, action.Target)))
                throw new ArgumentException("Одна настройка не может повторяться в профиле.", nameof(profile));
        }
        return new SavedProfile(profile.Name, Array.AsReadOnly(actions));
    }

    private static void ValidateLabel(string? label, int maximumLength, string description)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Length > maximumLength || label.Any(char.IsControl))
            throw new ArgumentException($"{description}: требуется от 1 до {maximumLength} символов без управляющих знаков.");
    }
}
