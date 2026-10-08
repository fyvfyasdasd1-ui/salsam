using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Salsam.Core;

namespace Salsam.App;

public sealed class Command(Action execute, Func<bool>? enabled = null) : ICommand
{
    public bool CanExecute(object? parameter) => enabled?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}
public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { field = value; Notify(name); }
}
public sealed class PendingChange(string title, string kind, string target, string after) : Observable
{
    public string Title { get; } = title;
    public string Kind { get; } = kind;
    public string Target { get; } = target;
    public string After { get; } = after;
    private bool included = true;
    public bool Included { get => included; set { Set(ref included, value); CommandManager.InvalidateRequerySuggested(); } }
}
public sealed class OptimizationItem : Observable
{
    public TweakDefinition Definition { get; }
    public string Id => Definition.Id;
    public string Title => Definition.Title;
    public string Target => Definition.Target;
    public string Kind => Definition.Kind;
    public string Desired => Definition.DesiredValue;
    public string Category => Definition.Category;
    public string Description => Definition.Description;
    public string Effect => Definition.Effect;
    public string Tradeoff => Definition.Tradeoff;
    public string Restart => Definition.Restart;
    public string DesiredLabel => ActionLabel(Desired);
    public string ValueDetails => Current == null ? "Значение не получено" :
        Definition.ValueType == TweakValueType.MouseAcceleration ? $"Пороговые значения и режим Windows: {Current}. Пороги сохраняются при переключении ускорения." :
        Definition.ValueType == TweakValueType.Boolean ? $"Текущее состояние: {State}" : $"Значение Windows: {Current} {Definition.Unit}";
    public bool CanToggle => Definition.ValueType is TweakValueType.Boolean or TweakValueType.MouseAcceleration;
    public bool CanSelect => IsAvailable && !IsAlreadyConfigured;
    public ICommand QueueEnableCommand { get; }
    public ICommand QueueDisableCommand { get; }
    private string state = "Ещё не проверено";
    private bool available, selected, alreadyConfigured;
    public string State { get => state; set => Set(ref state, value); }
    public bool IsAvailable { get => available; private set => Set(ref available, value); }
    public bool IsAlreadyConfigured { get => alreadyConfigured; private set => Set(ref alreadyConfigured, value); }
    public bool IsSelected { get => selected; set { Set(ref selected, value); CommandManager.InvalidateRequerySuggested(); } }
    public string? Current { get; private set; }
    public OptimizationItem(TweakDefinition definition, Action<OptimizationItem, string> queue, Func<bool> busy)
    {
        Definition = definition;
        QueueEnableCommand = new Command(() => queue(this, EnableValue), () => !busy() && IsAvailable && CanToggle && !TweakStates.Equivalent(Kind, Target, Current!, EnableValue));
        QueueDisableCommand = new Command(() => queue(this, DisableValue), () => !busy() && IsAvailable && CanToggle && !TweakStates.Equivalent(Kind, Target, Current!, DisableValue));
    }
    private string EnableValue => Definition.ValueType == TweakValueType.MouseAcceleration ? "[0,0,1]" : "true";
    private string DisableValue => Definition.ValueType == TweakValueType.MouseAcceleration ? "[0,0,0]" : "false";
    public string ActionLabel(string value) => Definition.ValueType switch
    {
        TweakValueType.Boolean => value == "true" ? "Включить" : "Выключить",
        TweakValueType.MouseAcceleration => TweakStates.Equivalent(Kind, Target, value, "[0,0,0]") ? "Выключить" : "Включить",
        TweakValueType.PowerScheme => "Выбрать схему",
        _ => Target == "mouse-trails" && value == "0" ? "Выключить" : $"Установить {value} {Definition.Unit}".Trim()
    };
    public void Update(string? current, string? error = null)
    {
        Current = current;
        IsAvailable = error == null && current != null;
        var result = IsAvailable ? TweakStates.Evaluate(Definition, current!) : null;
        State = result?.Label ?? "Недоступно: " + (error ?? "Windows не вернула значение");
        IsAlreadyConfigured = result?.AlreadyConfigured ?? false;
        if (!CanSelect) IsSelected = false;
        Notify(nameof(CanSelect)); Notify(nameof(ValueDetails));
        CommandManager.InvalidateRequerySuggested();
    }
}

public sealed class MainViewModel : Observable
{
    public string[] Sections { get; } = ["Обзор", "Оптимизация", "Игры", "Автозагрузка", "Очистка", "Мониторинг", "Восстановление"];
    private string section = "Обзор", search = "", status = "Загрузка сведений…", hardware = "Получение данных Windows…", cpu = "Ожидание", ram = "Ожидание", activePower = "Проверка…", restoreStatus = "Проверка не выполнялась", before = "До: нет измерений", after = "После: нет измерений";
    private bool busy;
    public string Section { get => section; set => Set(ref section, value); }
    public string Search { get => search; set { Set(ref search, value); NotifyCatalog(); } }
    public string Status { get => status; private set => Set(ref status, value); }
    public string Hardware { get => hardware; private set => Set(ref hardware, value); }
    public string Cpu { get => cpu; private set => Set(ref cpu, value); }
    public string Ram { get => ram; private set => Set(ref ram, value); }
    public string ActivePower { get => activePower; private set => Set(ref activePower, value); }
    public string RestoreStatus { get => restoreStatus; private set => Set(ref restoreStatus, value); }
    public string Before { get => before; private set => Set(ref before, value); }
    public string After { get => after; private set => Set(ref after, value); }
    public bool Busy { get => busy; private set { Set(ref busy, value); CommandManager.InvalidateRequerySuggested(); } }
    public ObservableCollection<PowerPlan> Plans { get; } = [];
    public ObservableCollection<StartupEntry> Startup { get; } = [];
    public ObservableCollection<TempEntry> Temps { get; } = [];
    public ObservableCollection<PendingChange> Pending { get; } = [];
    public ObservableCollection<ChangeRecord> History { get; } = [];
    private PowerPlan? selectedPlan;
    private StartupEntry? selectedStartup;
    private TempEntry? selectedTemp;
    private ChangeRecord? selectedRecord;
    public PowerPlan? SelectedPlan { get => selectedPlan; set { Set(ref selectedPlan, value); CommandManager.InvalidateRequerySuggested(); } }
    public StartupEntry? SelectedStartup { get => selectedStartup; set { Set(ref selectedStartup, value); CommandManager.InvalidateRequerySuggested(); } }
    public TempEntry? SelectedTemp { get => selectedTemp; set { Set(ref selectedTemp, value); CommandManager.InvalidateRequerySuggested(); } }
    public ChangeRecord? SelectedRecord { get => selectedRecord; set { Set(ref selectedRecord, value); CommandManager.InvalidateRequerySuggested(); } }
    public PointCollection BeforePoints { get; private set; } = [];
    public PointCollection AfterPoints { get; private set; } = [];
    public string GraphScale { get; private set; } = "Нет измерений";
    private FrameReport? beforeReport, afterReport;
    private readonly WindowsBackend backend = new();
    private readonly ChangeJournal journal = new(WindowsBackend.Data);
    private readonly ChangeService changes;
    private readonly MonitorService monitor = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool monitoringBusy, stopped;
    private string selectedCategory = "Все", gpu = "Ожидание счётчика", disk = "Ожидание счётчика", powerSource = "Проверка питания…";
    private string cpuName = "Проверка…", gpuName = "Проверка…", ramTotal = "Проверка…", windowsName = "Проверка…";
    private string analysisSummary = "Запустите проверку: она считывает состояния и ничего не меняет.", lastScan = "Проверка ещё не выполнялась", tempSummary = "Ещё не просканировано";
    private string profileName = "Мой профиль", profileStatus = "Сохраните выбранные настройки из очереди.";
    private double cpuPercent, ramPercent, gpuPercent;
    private int recommendationCount;
    private readonly ProfileStore profiles = new(WindowsBackend.Data);
    private readonly PerformanceService performance = new();
    public string[] Categories { get; } = ["Все", "Интерфейс", "Мышь", "Клавиатура", "Проводник", "Электропитание"];
    public string SelectedCategory { get => selectedCategory; set { Set(ref selectedCategory, value); NotifyCatalog(); } }
    public string Gpu { get => gpu; private set => Set(ref gpu, value); }
    public string Disk { get => disk; private set => Set(ref disk, value); }
    public string PowerSource { get => powerSource; private set => Set(ref powerSource, value); }
    public string CpuName { get => cpuName; private set => Set(ref cpuName, value); }
    public string GpuName { get => gpuName; private set => Set(ref gpuName, value); }
    public string RamTotal { get => ramTotal; private set => Set(ref ramTotal, value); }
    public string WindowsName { get => windowsName; private set => Set(ref windowsName, value); }
    public string AnalysisSummary { get => analysisSummary; private set => Set(ref analysisSummary, value); }
    public string LastScan { get => lastScan; private set => Set(ref lastScan, value); }
    public string TempSummary { get => tempSummary; private set => Set(ref tempSummary, value); }
    public string ProfileName { get => profileName; set => Set(ref profileName, value); }
    public string ProfileStatus { get => profileStatus; private set => Set(ref profileStatus, value); }
    public double CpuPercent { get => cpuPercent; private set => Set(ref cpuPercent, value); }
    public double RamPercent { get => ramPercent; private set => Set(ref ramPercent, value); }
    public double GpuPercent { get => gpuPercent; private set => Set(ref gpuPercent, value); }
    public int RecommendationCount { get => recommendationCount; private set => Set(ref recommendationCount, value); }
    public int StartupCount => Startup.Count;
    public ObservableCollection<OptimizationItem> Optimizations { get; } = [];
    public int OptimizationCount => Optimizations.Count;
    public int FilteredCount => FilteredOptimizations.Count();
    public int AlreadyConfiguredCount => Optimizations.Count(o => o.IsAvailable && o.IsAlreadyConfigured);
    public IEnumerable<OptimizationItem> FilteredOptimizations => Optimizations.Where(s =>
        (SelectedCategory == "Все" || s.Category == SelectedCategory) &&
        (s.Title + s.Description + s.State).Contains(Search, StringComparison.OrdinalIgnoreCase));
    public ICommand AnalyzeCommand => RefreshCommand;
    public ICommand QueueSelectedCommand { get; }
    public ICommand OptimalTweaksCommand { get; }
    public ICommand DefaultTweaksCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand LoadProfileCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand QueuePowerCommand { get; }
    public ICommand QueueStartupCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand QueueTempCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand RestoreAllCommand { get; }
    public ICommand BalancedCommand { get; }
    public ICommand GamingCommand { get; }
    public ICommand CustomCommand { get; }
    public ICommand GameSettingsCommand { get; } = new Command(() => SafeOpen("ms-settings:gaming-gamemode"));
    public ICommand CaptureSettingsCommand { get; } = new Command(() => SafeOpen("ms-settings:gaming-gamedvr"));
    public ICommand StartupSettingsCommand { get; } = new Command(() => SafeOpen("ms-settings:startupapps"));
    public ICommand RestorePointCommand { get; }
    public ICommand ImportBeforeCommand { get; }
    public ICommand ImportAfterCommand { get; }
    public MainViewModel()
    {
        changes = new(journal, backend);
        foreach (var definition in TweakCatalog.Definitions) Optimizations.Add(CreateItem(definition));
        RefreshCommand = new Command(() => _ = Refresh(), () => !Busy);
        QueuePowerCommand = new Command(() => { if (SelectedPlan is { } p) Queue(new($"Питание → {p.Name}. Возможны нагрев и расход батареи; без перезагрузки.", "power", "active", p.Id)); }, () => !Busy && SelectedPlan != null);
        QueueStartupCommand = new Command(() => { if (SelectedStartup is { } s) Queue(new($"Отключить автозагрузку: {s.Name}. Фоновые функции могут пропасть после следующего входа.", "startup", s.Name, "null")); }, () => !Busy && SelectedStartup != null);
        ScanCommand = new Command(() => _ = Work(ScanTemps), () => !Busy);
        QueueTempCommand = new Command(() => { if (SelectedTemp is { } t) Queue(new($"В карантин: {t.Path} ({t.Bytes} байт). Место на диске не освобождается.", "temp", t.Path, "quarantine")); }, () => !Busy && SelectedTemp != null);
        ApplyCommand = new Command(() => _ = Apply(), () => !Busy && Pending.Any(p => p.Included));
        ClearCommand = new Command(() => Pending.Clear(), () => !Busy);
        RestoreCommand = new Command(() => { if (SelectedRecord is { } r && Confirm("Восстановить исходное значение?\n" + r.Target)) _ = RestoreOne(r); }, () => !Busy && SelectedRecord is { Status: not "Восстановлено" });
        RestoreAllCommand = new Command(() => _ = RestoreAll(), () => !Busy && History.Any(r => r.Status != "Восстановлено"));
        BalancedCommand = new Command(() => Profile("381b4222-f694-41f0-9685-ff5bb260df2e", true), () => !Busy);
        GamingCommand = new Command(() => Profile("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", false), () => !Busy);
        CustomCommand = new Command(() => { Section = "Оптимизация"; Status = "Выберите настройки, добавьте в очередь и сохраните свой профиль. Исключите ненужные действия галочкой."; });
        QueueSelectedCommand = new Command(() => { foreach (var item in Optimizations.Where(o => o.CanSelect && o.IsSelected)) QueueOptimization(item, item.Desired); }, () => !Busy && Optimizations.Any(o => o.CanSelect && o.IsSelected));
        OptimalTweaksCommand = new Command(() =>
        {
            foreach (var item in Optimizations.Where(o => o.CanSelect && RecommendedIds.Contains(o.Id))) QueueOptimization(item, item.Desired);
            Status = "Рекомендации добавлены в очередь. Изучите последствия и исключите ненужные действия.";
        }, () => !Busy);
        DefaultTweaksCommand = new Command(() => _ = RestoreTweaks(), () => !Busy && History.Any(r => IsProfileKind(r.Kind) && r.Status != "Восстановлено"));
        SaveProfileCommand = new Command(SaveProfile, () => !Busy && Pending.Any(p => p.Included && IsProfileKind(p.Kind)));
        LoadProfileCommand = new Command(LoadProfile, () => !Busy);
        RestorePointCommand = new Command(() => _ = Work(() => { var text = WindowsBackend.RestorePoints(); Application.Current.Dispatcher.Invoke(() => RestoreStatus = text); }), () => !Busy);
        ImportBeforeCommand = new Command(() => Import(true)); ImportAfterCommand = new Command(() => Import(false));
        timer.Tick += async (_, _) => await UpdateMonitoring();
        timer.Start();
    }
    public void Stop() { stopped = true; timer.Stop(); performance.Dispose(); }
    private static void SafeOpen(string uri) { try { WindowsBackend.OpenSettings(uri); } catch (Exception ex) { MessageBox.Show(ex.Message, "Настройки недоступны"); } }
    private static bool Confirm(string text) => MessageBox.Show(text, "Проверка изменений", MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK;
    private static void Replace<T>(ObservableCollection<T> list, IEnumerable<T> items) { list.Clear(); foreach (var item in items) list.Add(item); }
    private static readonly HashSet<string> RecommendedIds = ["menu-animation", "tooltip-animation", "client-area-animation", "minimize-animation", "combo-animation", "selection-fade", "show-extensions"];
    private static readonly HashSet<string> ProfileAnimations = ["menu-animation", "tooltip-animation", "client-area-animation", "minimize-animation", "combo-animation", "listbox-smooth-scrolling", "selection-fade"];
    private static bool IsProfileKind(string kind) => kind is "visual" or "input" or "shell" or "power" or "power-value";
    private OptimizationItem CreateItem(TweakDefinition definition) => new(definition, QueueOptimization, () => Busy);
    private void NotifyCatalog()
    {
        Notify(nameof(FilteredOptimizations)); Notify(nameof(FilteredCount));
        Notify(nameof(OptimizationCount)); Notify(nameof(AlreadyConfiguredCount));
    }
    private void Queue(PendingChange item)
    {
        var previous = Pending.FirstOrDefault(p => p.Kind == item.Kind && p.Target == item.Target);
        if (previous != null) Pending.Remove(previous);
        Pending.Add(item); Status = "Изменение добавлено в очередь. Проверьте список справа.";
    }
    private void Profile(string id, bool animations)
    {
        var unavailable = new List<string>();
        var plan = Plans.FirstOrDefault(p => p.Id == id);
        if (plan != null) Queue(new($"Питание → {plan.Name}. {(animations ? "Баланс между быстродействием и энергопотреблением." : "Высокая производительность может увеличить нагрев и расход батареи.")}", "power", "active", plan.Id));
        else unavailable.Add("нужная схема питания отсутствует");
        foreach (var item in Optimizations.Where(o => ProfileAnimations.Contains(o.Id) || (!animations && o.Id is "mouse-acceleration" or "mouse-trails")))
        {
            if (item.IsAvailable) QueueOptimization(item, item.Kind == "visual" ? (animations ? "true" : "false") : item.Desired);
            else unavailable.Add(item.Title);
        }
        Status = unavailable.Count == 0 ? "Профиль добавлен в очередь. Исключите любые действия перед подтверждением." : "Доступные действия добавлены. Недоступно: " + string.Join(", ", unavailable);
    }
    private void QueueOptimization(OptimizationItem item, string value)
    {
        if (!item.IsAvailable) return;
        if (item.Current != null && backend.Matches(item.Kind, item.Target, item.Current, value))
        {
            var obsolete = Pending.FirstOrDefault(p => p.Kind == item.Kind && p.Target == item.Target);
            if (obsolete != null) Pending.Remove(obsolete);
            Status = $"{item.Title}: нужное состояние уже установлено. Повторное применение не требуется.";
            return;
        }
        var scope = item.Kind == "power-value" ? $" Схема: {Plans.FirstOrDefault(p => p.Id == item.Target.Split('/')[0])?.Name ?? item.Target.Split('/')[0]}. Только питание от сети." : "";
        Queue(new($"{item.Title}: {item.ActionLabel(value)}.{scope} {item.Description} Последствия выбора: {item.Tradeoff} Перезапуск: {item.Restart}.", item.Kind, item.Target, value));
    }
    private void SaveProfile()
    {
        try
        {
            var actions = Pending.Where(p => p.Included && IsProfileKind(p.Kind)).Select(p =>
            {
                var item = Optimizations.FirstOrDefault(o => o.Kind == p.Kind && o.Target == p.Target);
                return new ProfileAction(p.Kind, p.Target, p.After, item == null ? "Схема питания: " + p.After : $"{item.Title}: {item.ActionLabel(p.After)}");
            }).ToArray();
            profiles.Save(new SavedProfile(ProfileName.Trim(), actions));
            ProfileStatus = $"Сохранено: {ProfileName.Trim()} · {actions.Length} действий. Применение выполняется отдельно через очередь.";
            Status = "Профиль сохранён. Автозагрузка и временные файлы в постоянный профиль не включаются.";
        }
        catch (Exception ex) { ProfileStatus = "Не удалось сохранить: " + ex.Message; Status = ProfileStatus; }
    }
    private void LoadProfile()
    {
        try
        {
            var saved = profiles.Read();
            if (saved == null) { ProfileStatus = "Сохранённого профиля ещё нет."; return; }
            ProfileName = saved.Name;
            var skipped = 0; var otherScheme = 0;
            foreach (var action in saved.Actions)
            {
                if (action.Kind == "power" && !Plans.Any(p => p.Id == action.After)) { skipped++; continue; }
                var item = Optimizations.FirstOrDefault(o => o.Kind == action.Kind && o.Target == action.Target && o.IsAvailable);
                if (action.Kind != "power" && item == null) { skipped++; if (action.Kind == "power-value") otherScheme++; continue; }
                if (action.Kind != "power") QueueOptimization(item!, action.After);
                else Queue(new($"Питание → {Plans.First(p => p.Id == action.After).Name}. Возможны нагрев и расход батареи.", action.Kind, action.Target, action.After));
            }
            ProfileStatus = $"Профиль «{saved.Name}» загружен в очередь. Недоступных действий: {skipped}.";
            if (otherScheme > 0) ProfileStatus += $" Параметры другой схемы ({otherScheme}): сначала примените выбор этой схемы, обновите состояния и загрузите профиль снова.";
            Status = ProfileStatus;
        }
        catch (Exception ex) { ProfileStatus = "Не удалось загрузить: " + ex.Message; Status = ProfileStatus; }
    }
    private void ScanTemps()
    {
        var files = WindowsBackend.PreviewTemps();
        Application.Current.Dispatcher.Invoke(() => { Replace(Temps, files); TempSummary = $"{files.Count} файлов · {files.Sum(f => f.Bytes) / 1048576.0:F1} МБ, доступных для переноса в карантин"; });
    }
    private async Task UpdateMonitoring()
    {
        if (monitoringBusy || stopped) return;
        monitoringBusy = true;
        try
        {
            try
            {
                var sample = await Task.Run(monitor.Sample);
                if (stopped) return;
                Cpu = sample.Cpu is { } c ? $"{c:F0}%" : "Прогрев счётчика";
                CpuPercent = sample.Cpu ?? 0;
                Ram = $"{sample.Ram:F0}% · {sample.UsedGb:F1} / {sample.TotalGb:F1} ГБ"; RamPercent = sample.Ram;
            }
            catch (Exception ex) { Cpu = "Недоступно"; Ram = ex.Message; CpuPercent = RamPercent = 0; }
            try
            {
                var sample = await Task.Run(performance.Sample);
                if (stopped) return;
                Gpu = sample.GpuPercent is { } g ? $"{g:F0}% · наиболее занятый 3D-счётчик" : sample.GpuStatus;
                GpuPercent = sample.GpuPercent ?? 0;
                Disk = sample.DiskReadMb is { } r && sample.DiskWriteMb is { } w ? $"Чтение {r:F1} · запись {w:F1} МБ/с" : sample.DiskStatus;
                PowerSource = PerformanceService.PowerStatus();
            }
            catch (Exception ex) { Gpu = "Недоступно: " + ex.Message; Disk = "Счётчики недоступны"; GpuPercent = 0; }
        }
        finally { monitoringBusy = false; }
    }
    public async Task Refresh()
    {
        await Work(() =>
        {
            var issues = new List<string>();
            string? activeId = null;
            try { var h = InventoryService.Read(); Application.Current.Dispatcher.Invoke(() => { Hardware = h.Details; CpuName = h.CpuName; GpuName = h.GpuName; RamTotal = h.RamTotal; WindowsName = h.WindowsName; }); } catch (Exception ex) { Application.Current.Dispatcher.Invoke(() => Hardware = "Сведения недоступны: " + ex.Message); issues.Add("оборудование"); }
            try { var plans = WindowsBackend.Plans(); activeId = backend.Read("power", "active"); Application.Current.Dispatcher.Invoke(() => { Replace(Plans, plans); ActivePower = plans.FirstOrDefault(p => p.Id == activeId)?.Name ?? activeId; }); } catch (Exception ex) { Application.Current.Dispatcher.Invoke(() => ActivePower = ex.Message); issues.Add("питание"); }
            try { var entries = WindowsBackend.Startup(); Application.Current.Dispatcher.Invoke(() => { Replace(Startup, entries); Notify(nameof(StartupCount)); }); } catch (Exception ex) { issues.Add("автозагрузка: " + ex.Message); }
            try { ScanTemps(); } catch (Exception ex) { issues.Add("временная папка: " + ex.Message); }
            var powerDefinitions = activeId == null ? Array.Empty<TweakDefinition>() : PowerSettingsService.Definitions(activeId).ToArray();
            Application.Current.Dispatcher.Invoke(() =>
            {
                // Keep row selections when the plan is unchanged; discard stale plan-specific targets.
                foreach (var stale in Optimizations.Where(o => o.Kind == "power-value" && !powerDefinitions.Any(d => d.Target == o.Target)).ToArray()) Optimizations.Remove(stale);
                foreach (var definition in powerDefinitions)
                    if (!Optimizations.Any(o => o.Kind == definition.Kind && o.Target == definition.Target)) Optimizations.Add(CreateItem(definition));
            });
            foreach (var item in Application.Current.Dispatcher.Invoke(() => Optimizations.ToArray()))
            {
                string? value = null, error = null;
                try
                {
                    if (item.Kind == "power" && !Application.Current.Dispatcher.Invoke(() => Plans.Any(p => p.Id == item.Desired)))
                        throw new NotSupportedException("Схема не предоставлена этим компьютером.");
                    value = backend.Read(item.Kind, item.Target);
                    var state = TweakStates.Evaluate(item.Definition, value);
                    if (state.Label is "Состояние неизвестно" or "Схема недоступна") throw new IOException("Windows вернула неизвестное состояние.");
                }
                catch (Exception ex) { error = ex.Message; }
                Application.Current.Dispatcher.Invoke(() => item.Update(value, error));
            }
            try
            {
                var saved = profiles.Read();
                if (saved != null) Application.Current.Dispatcher.Invoke(() => ProfileStatus = $"Сохранён профиль «{saved.Name}», {saved.Actions.Count} действий. Загрузка добавляет их в очередь.");
            }
            catch (Exception ex) { Application.Current.Dispatcher.Invoke(() => ProfileStatus = "Профиль недоступен: " + ex.Message); }
            Application.Current.Dispatcher.Invoke(() =>
            {
                RecommendationCount = Optimizations.Count(o => o.CanSelect);
                AnalysisSummary = $"Проверено {OptimizationCount} настроек: {AlreadyConfiguredCount} уже совпадают с предлагаемыми значениями, {RecommendationCount} доступны для выбора. Это сведения о настройках, а не оценка быстродействия. Автозагрузка: {StartupCount} записей.";
                LastScan = "Проверено: " + DateTime.Now.ToString("HH:mm");
                NotifyCatalog();
            });
            if (issues.Count > 0) throw new IOException("Не удалось прочитать: " + string.Join(", ", issues));
        });
    }
    private async Task Work(Action action)
    {
        if (Busy) return;
        Busy = true; Status = "Выполняется…";
        try { await Task.Run(action); Status = "Операция завершена."; }
        catch (Exception ex) { Status = "Ошибка: " + ex.Message + " Права администратора автоматически не запрашиваются. Если политика Windows запрещает действие, оно недоступно."; }
        finally
        {
            try { Replace(History, journal.Read().Reverse()); } catch (Exception ex) { Status = "Журнал недоступен: " + ex.Message; }
            Busy = false;
        }
    }
    private async Task Apply()
    {
        // Switch the scheme last so per-scheme edits and reverse-order rollback retain the active plan.
        var items = Pending.Where(p => p.Included).OrderBy(p => p.Kind == "power" ? 1 : 0).ToArray();
        var switchPlan = items.FirstOrDefault(p => p.Kind == "power");
        if (switchPlan != null && items.Any(p => p.Kind == "power-value" && !p.Target.StartsWith(switchPlan.After + "/", StringComparison.OrdinalIgnoreCase)))
        {
            Status = "В очереди параметры одной схемы и переход на другую. Сначала примените выбор схемы отдельно, обновите состояния, затем выберите её параметры. Либо исключите переход из очереди.";
            return;
        }
        if (!Confirm("Будут применены:\n\n" + string.Join("\n\n", items.Select(p => p.Title)) + "\n\nИсходные значения сохраняются в журнале. При ошибке последующие действия остановятся.")) return;
        var applied = 0; var skipped = 0;
        await Work(() =>
        {
            foreach (var item in items)
            {
                if (item.Kind == "temp")
                {
                    var file = new System.IO.FileInfo(item.Target);
                    if (!file.Exists || file.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-7)) throw new IOException("Временный файл изменился после просмотра; просканируйте папку повторно.");
                }
                var outcome = changes.ApplyIfNeeded(item.Kind, item.Target, item.After);
                if (outcome.AlreadyConfigured) skipped++; else applied++;
                Application.Current.Dispatcher.Invoke(() => Pending.Remove(item));
            }
        });
        // Do not replace an operation failure with a successful refresh message.
        var operationStatus = Status;
        await Refresh();
        var refreshStatus = Status;
        if (IsFailureStatus(operationStatus)) Status = operationStatus;
        else
        {
            Status = $"Применено и проверено: {applied}. Уже установлено, пропущено без повторной записи: {skipped}.";
            if (IsFailureStatus(refreshStatus)) Status += " Обновление сведений: " + refreshStatus;
        }
    }
    private async Task RestoreOne(ChangeRecord record)
    {
        await Work(() => changes.Restore(record));
        await RefreshPreservingError();
    }
    private async Task RefreshPreservingError()
    {
        var outcome = Status; await Refresh();
        if (IsFailureStatus(outcome)) Status = outcome;
    }
    private static bool IsFailureStatus(string value) => value.StartsWith("Ошибка:", StringComparison.Ordinal) || value.StartsWith("Журнал недоступен:", StringComparison.Ordinal);
    private async Task RestoreAll()
    {
        if (!Confirm("Вернуть исходные значения всех изменений в обратном порядке? Откат остановится при конфликте с внешними изменениями.")) return;
        await Work(() => { foreach (var record in journal.Read().Reverse().Where(r => r.Status != "Восстановлено")) changes.Restore(record); });
        await RefreshPreservingError();
    }
    private async Task RestoreTweaks()
    {
        if (!Confirm("Восстановить исходные значения настроек каталога из журнала? Изменения будут отменены в обратном порядке. При внешнем изменении откат остановится.")) return;
        await Work(() => { foreach (var record in journal.Read().Reverse().Where(r => IsProfileKind(r.Kind) && r.Status != "Восстановлено")) changes.Restore(record); });
        await RefreshPreservingError();
    }
    private void Import(bool isBefore)
    {
        var picker = new OpenFileDialog { Filter = "CSV измерений|*.csv" };
        if (picker.ShowDialog() != true) return;
        try
        {
            var report = FrameReport.Parse(System.IO.File.ReadAllText(picker.FileName));
            var text = $"{(isBefore ? "До" : "После")}: {report.Count:N0} кадров · средний {report.AverageFps:F1} FPS · 1% low {report.OnePercentLow:F1} FPS";
            if (isBefore) { beforeReport = report; Before = text; } else { afterReport = report; After = text; }
            var maximum = new[] { beforeReport, afterReport }.Where(r => r != null).Max(r => r!.Milliseconds.Max());
            BeforePoints = Plot(beforeReport, maximum); AfterPoints = Plot(afterReport, maximum);
            GraphScale = $"Общая шкала: 0–{maximum:F1} мс. X: нормированная длительность записи; Y: время кадра. При группировке показан самый медленный кадр.";
            Notify(nameof(BeforePoints)); Notify(nameof(AfterPoints)); Notify(nameof(GraphScale));
            Status = "Импорт выполнен. Сравнение имеет смысл только при одинаковых условиях и CSV одного процесса.";
        }
        catch (Exception ex) { Status = "Импорт не выполнен: " + ex.Message; }
    }
    private static PointCollection Plot(FrameReport? report, double maximum)
    {
        var points = new PointCollection(); if (report == null) return points;
        var step = Math.Max(1, (int)Math.Ceiling(report.Count / 600.0));
        for (var i = 0; i < report.Count; i += step)
            points.Add(new Point(i * 600.0 / (report.Count - 1), 160 - report.Milliseconds.Skip(i).Take(step).Max() / maximum * 155));
        return points;
    }
}
