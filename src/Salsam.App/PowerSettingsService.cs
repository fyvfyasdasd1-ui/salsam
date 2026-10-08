using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Salsam.Core;

namespace Salsam.App;

/// <summary>Allowlisted AC indices in the active Windows power scheme; DC indices are never written.</summary>
public static class PowerSettingsService
{
    // Windows SDK winnt.h: GUID_{PCIEXPRESS_SETTINGS_SUBGROUP,PCIEXPRESS_ASPM_POLICY},
    // GUID_{DISK_SUBGROUP,DISK_POWERDOWN_TIMEOUT}, GUID_{SLEEP_SUBGROUP,STANDBY_TIMEOUT},
    // GUID_{VIDEO_SUBGROUP,VIDEO_POWERDOWN_TIMEOUT}, GUID_PROCESSOR_SETTINGS_SUBGROUP,
    // GUID_PROCESSOR_THROTTLE_{MAXIMUM,MINIMUM}.
    // https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/winnt.h
    // USB selective suspend: standard Windows powercfg subgroup/setting identifiers.
    // Every identifier must actually exist in the selected scheme before a write is accepted.
    // Behavior and Microsoft's recommendation to retain USB selective suspend:
    // https://learn.microsoft.com/windows-hardware/drivers/usbcon/usb-selective-suspend
    private sealed record Setting(string Id, string Subgroup, string Guid, uint Maximum, string Desired,
        string Title, string Description, string Effect, string Tradeoff, string Unit = "");

    private static readonly Setting[] Settings =
    [
        new("usb-suspend-ac", "2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", 1, "0",
            "Приостановка USB при питании от сети", "0 — отключена, 1 — включена. Меняется только активная схема и только питание от сети.",
            "Может помочь диагностировать задержки пробуждения отдельных USB-устройств; увеличение FPS не предполагается.",
            "Microsoft рекомендует оставлять приостановку включённой. Отключение повышает расход энергии и нагрев; параметры батареи не меняются."),
        new("pcie-link-ac", "501a4d13-42af-4429-9fd1-a8218c268e20", "ee12f906-d277-404b-b6da-e5fa1a576df5", 2, "0",
            "Энергосбережение PCIe при питании от сети", "0 — отключено, 1 — умеренное, 2 — максимальное энергосбережение линии PCIe. Только активная схема, питание от сети.",
            "Исключает переходы линии PCIe в режим энергосбережения там, где это поддерживается; результат зависит от устройства.",
            "Может увеличить нагрев и потребление энергии. Значения для батареи остаются прежними; FPS проверяйте измерением."),
        new("disk-idle-ac", "0012ee47-9041-4b5d-9b77-535fba8b1442", "6738e2c4-e8a5-4a42-b16a-e040e769756e", uint.MaxValue, "0",
            "Остановка диска при питании от сети", "Время простоя в секундах; 0 — не останавливать диск. Только активная схема, питание от сети.",
            "Для поддерживаемого HDD может убрать ожидание раскрутки после простоя; SSD и драйвер могут игнорировать настройку.",
            "Диск дольше работает, возможны шум, нагрев и больший расход энергии. Режим батареи не меняется.", "секунд"),
        new("sleep-idle-ac", "238c9fa8-0aad-41ed-83f4-97be242c8f20", "29f6c1db-86da-48c5-9fdb-f2b67b1f44da", uint.MaxValue, "0",
            "Автоматический сон при питании от сети", "Время простоя в секундах; 0 — не переходить в автоматический сон. Только активная схема, питание от сети.",
            "Компьютер остаётся активным во время длительной задачи; частота кадров от этого не повышается.",
            "Без присмотра компьютер продолжает потреблять энергию и может нагреваться. Сон при работе от батареи не меняется.", "секунд"),
        new("display-idle-ac", "7516b95f-f776-4464-8c53-06167f40cc99", "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e", uint.MaxValue, "0",
            "Отключение экрана при питании от сети", "Время простоя в секундах; 0 — не выключать экран автоматически. Только активная схема, питание от сети.",
            "Экран остаётся включённым при просмотре и длительных задачах; FPS не изменяет.",
            "Повышается потребление энергии; статичное изображение создаёт риск выгорания OLED. Режим батареи не меняется.", "секунд"),
        new("cpu-max-ac", "54533251-82be-4824-96c1-47b60b740d00", "bc5038f7-23e0-4960-96da-33abaf5935ec", 100, "100",
            "Максимальное состояние CPU от сети", "Максимальное состояние процессора: 0–100%. Только активная схема, питание от сети.",
            "100% убирает ограничение этой настройки; реальную частоту выбирают Windows, прошивка и температурные ограничения.",
            "Возможны больший нагрев, шум и расход энергии. Настройки батареи прежние; прирост FPS не гарантируется.", "%"),
        new("cpu-min-ac", "54533251-82be-4824-96c1-47b60b740d00", "893dee8e-2bef-41e0-89c6-b55d0929964c", 100, "5",
            "Минимальное состояние CPU от сети", "Минимальное состояние процессора: 0–100%. Предлагается 5%; только активная схема, питание от сети.",
            "Разрешает снижать состояние процессора в простое, что может уменьшить нагрев и потребление энергии.",
            "Выход из простоя зависит от оборудования. Это выбор для снижения нагрева, а не обязательная игровая оптимизация. Режим батареи не меняется.", "%")
    ];

    private static readonly Regex GuidInText = new(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        RegexOptions.CultureInvariant);
    private static readonly Regex FooterIndex = new(@"0x([0-9a-f]{1,8})\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<TweakDefinition> Definitions(string planId)
    {
        if (!Guid.TryParseExact(planId, "D", out var plan) || plan == Guid.Empty)
            throw new ArgumentException("Требуется GUID действующей схемы питания.", nameof(planId));
        var canonicalPlan = plan.ToString("D");
        return Array.AsReadOnly(Settings.Select(setting => new TweakDefinition(setting.Id, "power-value",
            $"{canonicalPlan}/{setting.Subgroup}/{setting.Guid}/ac", setting.Title, "Электропитание", setting.Description,
            setting.Effect, setting.Tradeoff, setting.Desired, TweakValueType.Integer,
            "Без перезагрузки · применяется к активной схеме", setting.Unit)).ToArray());
    }

    public static string Read(string target)
    {
        var (plan, setting) = ParseTarget(target);
        string output;
        try { output = WindowsBackend.Run("powercfg.exe", "/query", plan, setting.Subgroup, setting.Guid); }
        catch (IOException)
        {
            // Older powercfg versions accept only a scheme and subgroup in /query.
            // Select the exact setting block ourselves; never read another setting's footer.
            try { output = WindowsBackend.Run("powercfg.exe", "/query", plan, setting.Subgroup); }
            catch (IOException ex) { throw new NotSupportedException("Параметр питания недоступен в этой схеме: " + ex.Message, ex); }
        }
        var block = new List<string>();
        var found = false;
        foreach (var line in output.Split('\n'))
        {
            var identifiers = GuidInText.Matches(line);
            if (!found)
            {
                if (!identifiers.Any(identifier => identifier.Value.Equals(setting.Guid, StringComparison.OrdinalIgnoreCase))) continue;
                found = true;
            }
            else if (identifiers.Count != 0) break;
            if (!string.IsNullOrWhiteSpace(line)) block.Add(line);
        }
        if (!found) throw new NotSupportedException("Настройка отсутствует в выбранной схеме питания или не поддерживается устройством.");
        if (block.Count < 3) throw new NotSupportedException("Windows не предоставила текущие значения питания от сети и батареи.");
        // The final two hexadecimal indices are AC then DC, independent of translated labels.
        var ac = FooterIndex.Match(block[^2]);
        var dc = FooterIndex.Match(block[^1]);
        if (!ac.Success || !dc.Success || !uint.TryParse(ac.Groups[1].Value, NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out var value))
            throw new NotSupportedException("Не удалось распознать текущий индекс питания от сети; значение не подставлено.");
        if (value > setting.Maximum)
            throw new NotSupportedException("Текущий индекс находится вне документированного диапазона этой настройки.");
        return value.ToString(CultureInfo.InvariantCulture);
    }

    public static void Write(string target, string value)
    {
        var (plan, setting) = ParseTarget(target);
        if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
            index > setting.Maximum || value != index.ToString(CultureInfo.InvariantCulture))
            throw new ArgumentOutOfRangeException(nameof(value), $"Требуется целый индекс от 0 до {setting.Maximum} без знаков и пробелов.");
        EnsureActive(plan);
        _ = Read(target); // A known GUID alone does not establish platform support.
        EnsureActive(plan);
        WindowsBackend.Run("powercfg.exe", "/setacvalueindex", plan, setting.Subgroup, setting.Guid, value);
        // Never reactivate a former scheme if somebody changed it during the write.
        EnsureActive(plan);
        WindowsBackend.Run("powercfg.exe", "/setactive", plan);
    }

    private static (string Plan, Setting Setting) ParseTarget(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        var parts = target.Split('/');
        if (parts.Length != 4 || parts[3] != "ac" ||
            !Guid.TryParseExact(parts[0], "D", out var plan) || plan == Guid.Empty ||
            parts[0] != plan.ToString("D"))
            throw new ArgumentException("Разрешён только формат GUID_схемы/GUID_группы/GUID_настройки/ac.", nameof(target));
        var setting = Settings.SingleOrDefault(candidate => candidate.Subgroup == parts[1] && candidate.Guid == parts[2]);
        if (setting == null) throw new ArgumentException("Параметр отсутствует в разрешённом списке настроек питания.", nameof(target));
        return (parts[0], setting);
    }

    private static void EnsureActive(string plan)
    {
        var output = WindowsBackend.Run("powercfg.exe", "/getactivescheme");
        var active = GuidInText.Match(output);
        if (!active.Success || !active.Value.Equals(plan, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Активная схема питания изменилась. Обновите сведения перед применением или откатом; другая схема не активируется автоматически.");
    }
}
