using System.Text.Json;
using Salsam.Core;

public static class StateChecks
{
    public static int Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "salsam-states-" + Guid.NewGuid().ToString("N"));
        var passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            Console.WriteLine("PASS " + name);
            passed++;
        }
        void Throws<T>(Action action, string name) where T : Exception
        {
            try { action(); }
            catch (T) { Check(true, name); return; }
            throw new Exception("Expected failure: " + name);
        }
        var animation = TweakCatalog.Definitions.Single(d => d.Id == "menu-animation");
        var acceleration = TweakCatalog.Definitions.Single(d => d.Id == "mouse-acceleration");
        var trails = TweakCatalog.Definitions.Single(d => d.Id == "mouse-trails");
        var delay = TweakCatalog.Definitions.Single(d => d.Id == "keyboard-delay");
        try
        {
            var off = TweakStates.Evaluate(animation, "false");
            Check(off.Label == "Уже выключено" && off.AlreadyConfigured && off.Enabled == false, "disabled animation is already disabled from actual state");
            var on = TweakStates.Evaluate(animation, "true");
            Check(on.Label == "Включено" && !on.AlreadyConfigured && on.Enabled == true, "enabled animation is not reported disabled");
            var unknown = TweakStates.Evaluate(animation, "unavailable");
            Check(unknown.Enabled is null && !unknown.AlreadyConfigured && unknown.Label != "Уже выключено", "unreadable boolean state never appears disabled");
            var cues = TweakCatalog.Definitions.Single(d => d.Id == "keyboard-cues");
            Check(TweakStates.Evaluate(cues, "true").AlreadyConfigured && !TweakStates.Evaluate(cues, "false").AlreadyConfigured, "enabled goals use their own desired value");
            var disabledAcceleration = TweakStates.Evaluate(acceleration, "[6,10,0]");
            Check(disabledAcceleration.AlreadyConfigured && disabledAcceleration.Enabled == false, "disabled acceleration ignores preserved mouse thresholds");
            Check(!TweakStates.Evaluate(acceleration, "[6,10,2]").AlreadyConfigured && TweakStates.Evaluate(acceleration, "[6,10,2]").Enabled == true, "nonzero mouse acceleration modes are enabled");
            Check(TweakStates.Equivalent("input", "mouse-acceleration", "[6,10,2]", "[0,0,1]"), "enabled mouse acceleration modes compare semantically");
            Throws<FormatException>(() => TweakStates.Evaluate(acceleration, "broken"), "malformed mouse vector never becomes disabled");
            Throws<FormatException>(() => TweakStates.Evaluate(acceleration, "[0,0]"), "truncated mouse vector is rejected");
            Throws<FormatException>(() => TweakStates.Evaluate(acceleration, "[-1,0,0]"), "invalid mouse thresholds are rejected");
            Check(TweakStates.Evaluate(trails, "0") is { Label: "Уже выключено", AlreadyConfigured: true, Enabled: false }, "zero mouse trails are already disabled");
            Check(TweakStates.Evaluate(trails, "7").Enabled == true && !TweakStates.Evaluate(trails, "7").AlreadyConfigured, "visible mouse trails are enabled");
            Check(TweakStates.Evaluate(trails, "1") is { Label: "Уже выключено", AlreadyConfigured: true, Enabled: false }, "native mouse trail disabled alias is recognized while preserving exact rollback value");
            Check(TweakStates.Evaluate(delay, "0").Label == "Уже настроено" && TweakStates.Evaluate(delay, "3").Label.Contains("3 из 3"), "keyboard numeric states display actual values and goal");
            Throws<FormatException>(() => TweakStates.Evaluate(delay, "not-number"), "invalid numeric input state is rejected");
            Throws<FormatException>(() => TweakStates.Evaluate(delay, "4"), "out-of-range keyboard delay is rejected");
            var power = TweakCatalog.Definitions.Single(d => d.Kind == "power");
            Check(TweakStates.Evaluate(power, power.DesiredValue.ToUpperInvariant()).AlreadyConfigured, "power state compares actual scheme GUIDs");
            Check(!TweakStates.Evaluate(power, "missing").AlreadyConfigured, "unavailable power scheme is not reported configured");
            Check(TweakStates.Evaluate(delay with { Kind = "power-value", Target = "dynamic", DesiredValue = "0", Unit = "с" }, "4294967295").Label.Contains("4294967295 с"), "numeric power values cover the complete DWORD range");
            var sleepPower = delay with { Kind = "power-value", Target = "381b4222-f694-41f0-9685-ff5bb260df2e/238c9fa8-0aad-41ed-83f4-97be242c8f20/29f6c1db-86da-48c5-9fdb-f2b67b1f44da/ac", DesiredValue = "0", Unit = "с" };
            Check(TweakStates.Evaluate(sleepPower, "0") is { Label: "Уже выключено", AlreadyConfigured: true, Enabled: false }, "zero power timeout state is already disabled");
            var minimumCpu = sleepPower with { Target = "381b4222-f694-41f0-9685-ff5bb260df2e/54533251-82be-4824-96c1-47b60b740d00/893dee8e-2bef-41e0-89c6-b55d0929964c/ac", DesiredValue = "5", Unit = "%" };
            Check(TweakStates.Evaluate(minimumCpu, "0").Label == "Сейчас: 0 %" && !TweakStates.Evaluate(minimumCpu, "0").AlreadyConfigured, "zero CPU power percent is a numeric value rather than a disabled feature");

            var journal = new ChangeJournal(Path.Combine(directory, "noop"));
            var backend = new StateBackend { Value = "false" };
            var service = new ChangeService(journal, backend);
            var noOp = service.ApplyIfNeeded("visual", "menu-animation", "false");
            Check(noOp.Record is null && noOp.AlreadyConfigured && noOp.CurrentValue == "false" && backend.Writes == 0 && journal.Read().Count == 0, "already configured setting performs no write or journal entry");
            backend.ReadFailure = true;
            Throws<IOException>(() => service.ApplyIfNeeded("visual", "menu-animation", "false"), "fresh read failure prevents an optimization write");
            Check(backend.Writes == 0 && journal.Read().Count == 0, "fresh read failure does not create a misleading journal entry");
            backend.ReadFailure = false;
            backend.Value = "true";
            var staleCard = TweakStates.Evaluate(animation, backend.Value);
            backend.Value = "false";
            var changedElsewhere = service.ApplyIfNeeded("visual", "menu-animation", "false");
            Check(!staleCard.AlreadyConfigured && changedElsewhere.AlreadyConfigured && backend.Writes == 0, "queued stale enabled state is reread before application");
            backend.Value = "true";
            var apply = service.ApplyIfNeeded("visual", "menu-animation", "false");
            Check(!apply.AlreadyConfigured && apply.Record?.Before == "true" && apply.Record.ActualAfter == "false" && backend.Writes == 1, "state changed after a card read uses the fresh rollback original");
            backend.Value = "true";
            service.Restore(apply.Record!);
            Check(backend.Writes == 1 && journal.Read().Single().Status == "Восстановлено", "rollback avoids a needless write when original is already restored");

            journal = new ChangeJournal(Path.Combine(directory, "vector"));
            backend = new StateBackend { Value = "[6,10,1]", PreserveThresholds = true };
            service = new ChangeService(journal, backend);
            var vectorRecord = service.ApplyIfNeeded("input", "mouse-acceleration", "[0,0,0]").Record!;
            Check(vectorRecord.Before == "[6,10,1]" && vectorRecord.After == "[0,0,0]" && vectorRecord.ActualAfter == "[6,10,0]", "semantic write verification records the exact observed mouse vector");
            backend.Value = "[7,10,0]";
            Throws<InvalidOperationException>(() => service.Restore(vectorRecord), "external mouse threshold changes block rollback despite same enabled state");
            Check(backend.Value == "[7,10,0]" && backend.Writes == 1, "conflicting external mouse vector is not overwritten");
            backend.Value = vectorRecord.ActualAfter!;
            service.Restore(vectorRecord);
            Check(backend.Value == vectorRecord.Before && journal.Read().Single().Status == "Восстановлено", "mouse rollback restores the exact original vector");
            backend.Value = "[6,10,0]";
            var vectorNoOp = service.ApplyIfNeeded("input", "mouse-acceleration", "[0,0,0]");
            Check(vectorNoOp.AlreadyConfigured && journal.Read().Count == 1 && backend.Writes == 2, "semantic mouse no-op preserves thresholds and avoids another journal entry");
            backend.Value = "broken";
            Throws<FormatException>(() => service.ApplyIfNeeded("input", "mouse-acceleration", "[0,0,0]"), "malformed actual mouse state prevents application");
            Check(journal.Read().Count == 1 && backend.Writes == 2, "malformed actual mouse state performs no write or journal entry");

            journal = new ChangeJournal(Path.Combine(directory, "trails"));
            backend = new StateBackend { Value = "1" };
            service = new ChangeService(journal, backend);
            var trailNoOp = service.ApplyIfNeeded("input", "mouse-trails", "0");
            Check(trailNoOp.AlreadyConfigured && backend.Value == "1" && backend.Writes == 0 && journal.Read().Count == 0, "disabled trail alias performs no write and retains its exact original state");
            var trailRecord = service.ApplyIfNeeded("input", "mouse-trails", "5").Record!;
            service.Restore(trailRecord);
            Check(backend.Value == "1" && trailRecord.Before == "1", "trail rollback preserves the native disabled alias exactly");

            journal = new ChangeJournal(Path.Combine(directory, "writefailure"));
            backend = new StateBackend { Value = "true", FailAfterWrite = true };
            service = new ChangeService(journal, backend);
            Throws<IOException>(() => service.ApplyIfNeeded("visual", "menu-animation", "false"), "partial write failure is surfaced");
            Check(journal.Read().Single().ActualAfter == "false" && journal.Read().Single().Before == "true", "partial write failure retains actual observed recovery state");
            backend.FailAfterWrite = false;
            service.Restore(journal.Read().Single());
            Check(backend.Value == "true", "observed partial write can be restored exactly");

            journal = new ChangeJournal(Path.Combine(directory, "legacy"));
            backend = new StateBackend { Value = "false" };
            service = new ChangeService(journal, backend);
            service.Restore(new ChangeRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, "visual", "menu-animation", "true", "false", "Применено"));
            Check(backend.Value == "true", "older journal records without ActualAfter remain recoverable");

            var profileStore = new ProfileStore(Path.Combine(directory, "profile"));
            var allCatalogActions = TweakCatalog.Definitions.Select(d => new ProfileAction(d.Kind, d.Target, d.DesiredValue, d.Title)).ToArray();
            profileStore.Save(new SavedProfile("Расширенные настройки", allCatalogActions));
            Check(profileStore.Read()?.Actions.Count == 30, "all thirty implemented catalog choices can be saved without commands or file paths");
            Throws<ArgumentException>(() => profileStore.Save(new SavedProfile("Ошибка", [new("input", "keyboard-speed", "32", "Клавиатура")])), "profiles reject unsupported input values");
            Throws<ArgumentException>(() => profileStore.Save(new SavedProfile("Ошибка", [new("input", "mouse-acceleration", "[6,10,0]", "Мышь")])), "profiles store desired mouse modes rather than machine-specific thresholds");
            Throws<ArgumentException>(() => profileStore.Save(new SavedProfile("Ошибка", [new("shell", "unknown", "false", "Проводник")])), "profiles reject arbitrary shell targets");
            const string cpuTarget = "381b4222-f694-41f0-9685-ff5bb260df2e/54533251-82be-4824-96c1-47b60b740d00/bc5038f7-23e0-4960-96da-33abaf5935ec/ac";
            profileStore.Save(new SavedProfile("Питание", [new("power-value", cpuTarget, "100", "Максимум CPU")]));
            Check(profileStore.Read()?.Actions.Single().After == "100", "profiles allow documented AC power setting choices");
            Throws<ArgumentException>(() => profileStore.Save(new SavedProfile("Ошибка", [new("power-value", cpuTarget, "101", "CPU")])), "profiles enforce documented power setting ranges");
            Throws<ArgumentException>(() => profileStore.Save(new SavedProfile("Ошибка", [new("power-value", cpuTarget, "0100", "CPU")])), "profiles reject noncanonical power setting indexes");
            Throws<ArgumentException>(() => profileStore.Save(new SavedProfile("Ошибка", [new("power-value", cpuTarget[..^2] + "dc", "100", "CPU")])), "profiles reject unimplemented battery power targets");
            Throws<ArgumentException>(() => profileStore.Save(new SavedProfile("Ошибка", [new("power-value", cpuTarget.Replace("bc5038f7", "ac5038f7"), "100", "CPU")])), "profiles reject unknown power setting GUIDs");
            return passed;
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private sealed class StateBackend : ISettingBackend, ISettingValueComparer
    {
        public string Value = "";
        public int Writes;
        public bool ReadFailure, PreserveThresholds, FailAfterWrite;
        public string Read(string kind, string target)
        {
            if (ReadFailure) throw new IOException("Simulated unavailable actual state.");
            return Value;
        }
        public void Write(string kind, string target, string value)
        {
            Writes++;
            if (PreserveThresholds && kind == "input" && target == "mouse-acceleration")
            {
                var actual = JsonSerializer.Deserialize<int[]>(Value)!;
                actual[2] = JsonSerializer.Deserialize<int[]>(value)![2];
                Value = JsonSerializer.Serialize(actual);
            }
            else Value = value;
            if (FailAfterWrite) throw new IOException("Simulated failure after write.");
        }
        public bool Matches(string kind, string target, string actual, string desired) => TweakStates.Equivalent(kind, target, actual, desired);
    }
}
