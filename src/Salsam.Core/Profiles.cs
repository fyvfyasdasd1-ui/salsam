using System.Text.Json;
using System.Globalization;

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
    private static readonly HashSet<string> VisualTargets = TweakCatalog.Definitions.Where(d => d.Kind == "visual").Select(d => d.Target).ToHashSet(StringComparer.Ordinal);
    private static readonly HashSet<string> ShellTargets = TweakCatalog.Definitions.Where(d => d.Kind == "shell").Select(d => d.Target).ToHashSet(StringComparer.Ordinal);
    private static readonly Dictionary<(string Subgroup, string Setting), uint> PowerValueLimits = new()
    {
        [("2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226")] = 1,
        [("501a4d13-42af-4429-9fd1-a8218c268e20", "ee12f906-d277-404b-b6da-e5fa1a576df5")] = 2,
        [("0012ee47-9041-4b5d-9b77-535fba8b1442", "6738e2c4-e8a5-4a42-b16a-e040e769756e")] = uint.MaxValue,
        [("238c9fa8-0aad-41ed-83f4-97be242c8f20", "29f6c1db-86da-48c5-9fdb-f2b67b1f44da")] = uint.MaxValue,
        [("7516b95f-f776-4464-8c53-06167f40cc99", "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e")] = uint.MaxValue,
        [("54533251-82be-4824-96c1-47b60b740d00", "bc5038f7-23e0-4960-96da-33abaf5935ec")] = 100,
        [("54533251-82be-4824-96c1-47b60b740d00", "893dee8e-2bef-41e0-89c6-b55d0929964c")] = 100
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
                "shell" => ShellTargets.Contains(action.Target) && action.After is "true" or "false",
                "input" => ValidInput(action.Target, action.After),
                "power-value" => ValidPowerValue(action.Target, action.After),
                _ => false
            };
            if (!valid)
                throw new ArgumentException("Профиль может содержать только поддерживаемые настройки питания, интерфейса, ввода и Проводника.", nameof(profile));
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

    private static bool ValidInput(string target, string value)
    {
        if (target == "mouse-acceleration")
        {
            try
            {
                var vector = TweakStates.MouseVector(value);
                return vector[0] == 0 && vector[1] == 0 && vector[2] is 0 or 1;
            }
            catch (FormatException) { return false; }
        }
        if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number.ToString(CultureInfo.InvariantCulture) != value) return false;
        return target switch
        {
            "mouse-trails" => number == 0 || number is >= 2 and <= 16,
            "keyboard-delay" => number <= 3,
            "keyboard-speed" => number <= 31,
            _ => false
        };
    }

    private static bool ValidPowerValue(string target, string value)
    {
        if (target is null || value is null) return false;
        var parts = target.Split('/');
        if (parts.Length != 4 || parts[3] != "ac") return false;
        for (var i = 0; i < 3; i++)
            if (!Guid.TryParseExact(parts[i], "D", out var guid) || guid == Guid.Empty || parts[i] != guid.ToString("D"))
                return false;
        return PowerValueLimits.TryGetValue((parts[1], parts[2]), out var maximum) &&
            uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number <= maximum &&
            number.ToString(CultureInfo.InvariantCulture) == value;
    }
}
