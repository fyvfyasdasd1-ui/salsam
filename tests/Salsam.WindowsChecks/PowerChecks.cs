using System.Globalization;
using System.IO;
using Salsam.App;
using Salsam.Core;

public static class PowerChecks
{
    public static void Run()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Нужна Windows 10/11.");
        var backend = new WindowsBackend();
        var plan = backend.Read("power", "active");
        var definitions = PowerSettingsService.Definitions(plan);
        Check(definitions.Count == 7 && definitions.Select(item => item.Id).Distinct().Count() == 7,
            "seven uniquely identified AC power settings");
        Check(definitions.All(item => item.Kind == "power-value" && item.ValueType == TweakValueType.Integer &&
            item.Target.StartsWith(plan + "/", StringComparison.Ordinal) && item.Target.EndsWith("/ac", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(item.Description) && !string.IsNullOrWhiteSpace(item.Tradeoff)), "AC metadata describes settings and tradeoffs");
        Reject(() => PowerSettingsService.Definitions("not-a-plan"), "invalid plan rejected");
        Reject(() => PowerSettingsService.Read(definitions[0].Target[..^2] + "dc"), "DC target rejected before native access");
        Reject(() => PowerSettingsService.Read(plan + "/" + Guid.NewGuid() + "/" + Guid.NewGuid() + "/ac"), "unknown power GUID pair rejected");
        Reject(() => PowerSettingsService.Write(definitions[0].Target, "2"), "USB index outside whitelist range rejected");
        Reject(() => PowerSettingsService.Write(definitions.Single(item => item.Id == "pcie-link-ac").Target, "3"), "PCIe index outside whitelist range rejected");
        Reject(() => PowerSettingsService.Write(definitions.Single(item => item.Id == "cpu-max-ac").Target, "101"), "CPU percent outside whitelist range rejected");
        Reject(() => PowerSettingsService.Write(definitions.Single(item => item.Id == "display-idle-ac").Target, "4294967296"), "timeout outside DWORD range rejected");
        Reject(() => PowerSettingsService.Write(definitions[0].Target, "00"), "noncanonical power index rejected");

        var supported = new List<TweakDefinition>();
        foreach (var definition in definitions)
        {
            try
            {
                var value = PowerSettingsService.Read(definition.Target);
                Check(uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _), "native AC index: " + definition.Id);
                supported.Add(definition);
            }
            catch (NotSupportedException ex) { Console.WriteLine("UNAVAILABLE " + definition.Id + ": " + ex.Message); }
        }
        if (supported.Count == 0) throw new InvalidOperationException("Ни один разрешённый параметр питания недоступен; рабочая среда питания не проверена.");
        var candidate = supported.FirstOrDefault(item => item.Id == "display-idle-ac") ??
            supported.FirstOrDefault(item => item.Id == "sleep-idle-ac");
        if (candidate == null)
        {
            Console.WriteLine("UNAVAILABLE reversible power write check: neither display nor sleep timeout is provided. Other native AC indices were read; no write/restore claim is made.");
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "Salsam-power-check-" + Guid.NewGuid());
        var journal = new ChangeJournal(directory);
        var service = new ChangeService(journal, backend);
        var original = PowerSettingsService.Read(candidate.Target);
        var number = uint.Parse(original, CultureInfo.InvariantCulture);
        // Increase by ten minutes when possible, so the integration check does not
        // put an otherwise idle runner to sleep or blank its display during recovery.
        var different = (number <= uint.MaxValue - 600 ? number + 600 : number - 600).ToString(CultureInfo.InvariantCulture);
        try
        {
            var outcome = service.ApplyIfNeeded("power-value", candidate.Target, different);
            Check(!outcome.AlreadyConfigured && outcome.Record != null && outcome.Record.Before == original,
                "different supported timeout captures exact original AC value");
            Check(PowerSettingsService.Read(candidate.Target) == different, "actual AC timeout write verified");
            var noOp = service.ApplyIfNeeded("power-value", candidate.Target, different);
            Check(noOp.AlreadyConfigured && noOp.Record == null && journal.Read().Count == 1,
                "repeat AC apply is a no-op without another journal entry");
            service.Restore(outcome.Record!);
            Check(PowerSettingsService.Read(candidate.Target) == original, "exact original AC timeout restored");
            Check(backend.Read("power", "active") == plan, "power check retains active scheme");
        }
        finally
        {
            var restored = false;
            try
            {
                var record = journal.Read().LastOrDefault();
                if (record != null && record.Status != "Восстановлено") service.Restore(record);
                Check(PowerSettingsService.Read(candidate.Target) == original, "power finally verifies restoration even after a failed check");
                restored = true;
            }
            finally
            {
                if (restored && Directory.Exists(directory)) Directory.Delete(directory, true);
                else if (!restored) Console.WriteLine("RECOVERY JOURNAL RETAINED: " + directory);
            }
        }
    }

    private static void Reject(Action action, string name)
    {
        try { action(); }
        catch (ArgumentException) { Check(true, name); return; }
        throw new InvalidOperationException(name + " unexpectedly accepted");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine("PASS " + name);
    }
}
