using Salsam.Core;

public static class ProfileChecks
{
    public static int Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "salsam-profiles-" + Guid.NewGuid().ToString("N"));
        var passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            Console.WriteLine("PASS " + name);
            passed++;
        }
        void Reject(Action action, string name)
        {
            try { action(); }
            catch (ArgumentException) { Check(true, name); return; }
            throw new Exception("Expected profile validation failure: " + name);
        }
        var power = new ProfileAction("power", "active", "381b4222-f694-41f0-9685-ff5bb260df2e", "Сбалансированное питание");
        var visual = new ProfileAction("visual", "menu-animation", "false", "Анимация меню");
        try
        {
            var store = new ProfileStore(directory);
            Check(store.Read() is null, "missing custom profile is optional");
            var profile = new SavedProfile("Мой профиль", [power, visual]);
            store.Save(profile);
            var restored = new ProfileStore(directory).Read();
            Check(restored?.Name == profile.Name && restored.Actions.SequenceEqual(profile.Actions), "custom profile roundtrip retains user choices");
            var originalFile = File.ReadAllBytes(Path.Combine(directory, "custom-profile.json"));
            Reject(() => store.Save(new SavedProfile("Профиль", [power with { Kind = "startup", Target = "HKCU\\Run\\App", After = "private-command.exe" }])), "persistent profile rejects startup commands");
            Reject(() => store.Save(new SavedProfile("Профиль", [visual with { Kind = "temp", Target = "C:\\Users\\private\\file.tmp" }])), "persistent profile rejects temporary file paths");
            Reject(() => store.Save(new SavedProfile("Профиль", [visual with { Target = "registry-path" }])), "persistent profile rejects unknown visual target");
            Reject(() => store.Save(new SavedProfile("Профиль", [visual with { After = "1" }])), "persistent profile rejects unsupported boolean value");
            Reject(() => store.Save(new SavedProfile("Профиль", [power with { After = "invalid-scheme" }])), "persistent profile rejects invalid power scheme");
            Reject(() => store.Save(new SavedProfile("Профиль", [power with { Target = "other" }])), "persistent profile rejects unknown power target");
            Reject(() => store.Save(new SavedProfile("Профиль", [visual, visual with { After = "true" }])), "persistent profile rejects duplicate setting");
            Reject(() => store.Save(new SavedProfile(" ", [visual])), "persistent profile requires a name");
            Reject(() => store.Save(new SavedProfile(new string('x', 81), [visual])), "persistent profile limits name length");
            Reject(() => store.Save(new SavedProfile("Профиль", [visual with { Title = "line\nbreak" }])), "persistent profile rejects control characters in labels");
            Reject(() => store.Save(new SavedProfile("Профиль", Enumerable.Repeat(visual, 65).ToArray())), "persistent profile limits action count");
            Check(File.ReadAllBytes(Path.Combine(directory, "custom-profile.json")).SequenceEqual(originalFile), "invalid profile saves preserve prior file bytes");
            Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "profile save leaves no temporary files");
            store.Save(new SavedProfile("Без изменений", []));
            Check(store.Read()?.Actions.Count == 0, "empty custom profile can preserve an explicit choice");
            File.WriteAllText(Path.Combine(directory, "custom-profile.json"), "{\"Name\":\"Подмена\",\"Actions\":[{\"Kind\":\"startup\",\"Target\":\"x\",\"After\":\"y\",\"Title\":\"x\"}]}");
            try { store.Read(); throw new Exception("Expected invalid stored profile to fail"); }
            catch (InvalidDataException) { Check(true, "reading a modified profile enforces the same whitelist"); }
            return passed;
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
