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
    public bool Included { get => included; set => Set(ref included, value); }
}
public record SettingInfo(string Title, string Description, string Effect, string Tradeoff, string State, string Restart);

public sealed class MainViewModel : Observable
{
    public string[] Sections { get; } = ["Обзор", "Система", "Игры", "Автозагрузка", "Очистка", "Мониторинг", "Восстановление"];
    private string section = "Обзор", search = "", status = "Загрузка сведений…", hardware = "Получение данных Windows…", cpu = "Ожидание", ram = "Ожидание", activePower = "Проверка…", restoreStatus = "Проверка не выполнялась", before = "До: нет измерений", after = "После: нет измерений";
    private bool busy;
    public string Section { get => section; set => Set(ref section, value); }
    public string Search { get => search; set { Set(ref search, value); Notify(nameof(FilteredSettings)); } }
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
    public PowerPlan? SelectedPlan { get; set; }
    public StartupEntry? SelectedStartup { get; set; }
    public TempEntry? SelectedTemp { get; set; }
    public ChangeRecord? SelectedRecord { get; set; }
    public PointCollection BeforePoints { get; private set; } = [];
    public PointCollection AfterPoints { get; private set; } = [];
    public string GraphScale { get; private set; } = "Нет измерений";
    private FrameReport? beforeReport, afterReport;
    private readonly WindowsBackend backend = new();
    private readonly ChangeJournal journal = new(WindowsBackend.Data);
    private readonly ChangeService changes;
    private readonly MonitorService monitor = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    public IReadOnlyList<SettingInfo> Settings { get; } = [
        new("Схема питания", "Переключает существующую схему Windows; новые схемы не создаются.", "Меняет политику энергопотребления. Прирост FPS не гарантирован.", "Высокая производительность может увеличить нагрев и расход батареи.", "Текущая схема показана в разделе «Система».", "Перезагрузка не требуется"),
        new("Автозагрузка пользователя", "Отключает выбранную запись HKCU Run. Службы, задачи и записи других пользователей не изменяются.", "Программа не будет автоматически запускаться при следующем входе.", "Могут стать недоступны её уведомления и фоновые функции.", "Список считывается из Windows; отключённые записи доступны в журнале.", "Действует при следующем входе; работающий процесс не завершается"),
        new("Карантин временных файлов", "Только файлы верхнего уровня %TEMP%, не изменявшиеся более 7 дней. Ссылки исключены.", "Убирает выбранные файлы из временной папки с возможностью возврата.", "Карантин на том же диске не освобождает место. Заблокированные файлы будут пропущены с ошибкой.", "Предварительный просмотр в разделе «Очистка».", "Перезагрузка не требуется"),
        new("Игровой режим / запись / оверлеи", "Открывает штатные настройки Windows. Изменения на этих страницах выполняются вручную.", "Позволяет проверить игровые настройки и фоновую запись.", "Отключение записи лишает возможности сохранять предыдущие моменты игры. Журнал приложения не отслеживает ручные изменения.", "Автоматическое определение состояния и управление оверлеями не реализованы.", "Зависит от выбранной настройки Windows"),
        new("Измерение FPS", "Импорт CSV с FrameTimeMs или MsBetweenPresents; минимум 100 кадров одной игры/процесса.", "Рассчитывает средний FPS и 1% low по измеренным временам кадров.", "Сравнивайте одинаковую сцену, разрешение, настройки, длительность и условия прогрева.", "Захват FPS в реальном времени не реализован. Поддерживается импорт.", "Перезагрузка не требуется")
    ];
    public IEnumerable<SettingInfo> FilteredSettings => Settings.Where(s => (s.Title + s.Description + s.State).Contains(Search, StringComparison.OrdinalIgnoreCase));
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
        RefreshCommand = new Command(() => _ = Refresh(), () => !Busy);
        QueuePowerCommand = new Command(() => { if (SelectedPlan is { } p) Queue(new($"Питание → {p.Name}. Возможны нагрев и расход батареи; без перезагрузки.", "power", "active", p.Id)); }, () => !Busy);
        QueueStartupCommand = new Command(() => { if (SelectedStartup is { } s) Queue(new($"Отключить автозагрузку: {s.Name}. Фоновые функции могут пропасть после следующего входа.", "startup", s.Name, "null")); }, () => !Busy);
        ScanCommand = new Command(() => _ = Work(() => { var files = WindowsBackend.PreviewTemps(); Application.Current.Dispatcher.Invoke(() => Replace(Temps, files)); }), () => !Busy);
        QueueTempCommand = new Command(() => { if (SelectedTemp is { } t) Queue(new($"В карантин: {t.Path} ({t.Bytes} байт). Место на диске не освобождается.", "temp", t.Path, "quarantine")); }, () => !Busy);
        ApplyCommand = new Command(() => _ = Apply(), () => !Busy && Pending.Any(p => p.Included));
        ClearCommand = new Command(() => Pending.Clear(), () => !Busy);
        RestoreCommand = new Command(() => { if (SelectedRecord is { } r && Confirm("Восстановить исходное значение?\n" + r.Target)) _ = Work(() => changes.Restore(r)); }, () => !Busy);
        RestoreAllCommand = new Command(() => _ = RestoreAll(), () => !Busy);
        BalancedCommand = new Command(() => Profile("381b4222-f694-41f0-9685-ff5bb260df2e"), () => !Busy);
        GamingCommand = new Command(() => Profile("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"), () => !Busy);
        CustomCommand = new Command(() => { Section = "Система"; Status = "Свой профиль: добавьте настройки в очередь и исключите ненужные перед применением. Сохранение профиля между запусками пока не реализовано."; });
        RestorePointCommand = new Command(() => _ = Work(() => { var text = WindowsBackend.RestorePoints(); Application.Current.Dispatcher.Invoke(() => RestoreStatus = text); }), () => !Busy);
        ImportBeforeCommand = new Command(() => Import(true)); ImportAfterCommand = new Command(() => Import(false));
        timer.Tick += (_, _) => { try { var sample = monitor.Sample(); Cpu = sample.Cpu is { } c ? $"{c:F0}%" : "Ожидание"; Ram = $"{sample.Ram:F0}% · {sample.UsedGb:F1} / {sample.TotalGb:F1} ГБ"; } catch (Exception ex) { Cpu = "Недоступно"; Ram = ex.Message; } };
        timer.Start();
    }
    public void Stop() => timer.Stop();
    private static void SafeOpen(string uri) { try { WindowsBackend.OpenSettings(uri); } catch (Exception ex) { MessageBox.Show(ex.Message, "Настройки недоступны"); } }
    private static bool Confirm(string text) => MessageBox.Show(text, "Проверка изменений", MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK;
    private static void Replace<T>(ObservableCollection<T> list, IEnumerable<T> items) { list.Clear(); foreach (var item in items) list.Add(item); }
    private void Queue(PendingChange item)
    {
        var previous = Pending.FirstOrDefault(p => p.Kind == item.Kind && p.Target == item.Target);
        if (previous != null) Pending.Remove(previous);
        Pending.Add(item); Status = "Изменение добавлено в очередь. Проверьте список справа.";
    }
    private void Profile(string id)
    {
        var plan = Plans.FirstOrDefault(p => p.Id == id);
        if (plan == null) { Status = "Эта схема недоступна на компьютере. Выберите существующую схему в разделе «Система»."; return; }
        Queue(new($"Профиль: питание → {plan.Name}. Другие настройки не меняются. Проверьте нагрев и расход батареи.", "power", "active", plan.Id));
    }
    public async Task Refresh()
    {
        await Work(() =>
        {
            var issues = new List<string>();
            try { var h = WindowsBackend.Hardware(); Application.Current.Dispatcher.Invoke(() => Hardware = h); } catch (Exception ex) { Application.Current.Dispatcher.Invoke(() => Hardware = "Сведения недоступны: " + ex.Message); issues.Add("оборудование"); }
            try { var plans = WindowsBackend.Plans(); var active = backend.Read("power", "active"); Application.Current.Dispatcher.Invoke(() => { Replace(Plans, plans); ActivePower = plans.FirstOrDefault(p => p.Id == active)?.Name ?? active; }); } catch (Exception ex) { Application.Current.Dispatcher.Invoke(() => ActivePower = ex.Message); issues.Add("питание"); }
            try { var entries = WindowsBackend.Startup(); Application.Current.Dispatcher.Invoke(() => Replace(Startup, entries)); } catch (Exception ex) { issues.Add("автозагрузка: " + ex.Message); }
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
        var items = Pending.Where(p => p.Included).ToArray();
        if (!Confirm("Будут применены:\n\n" + string.Join("\n\n", items.Select(p => p.Title)) + "\n\nИсходные значения сохраняются в журнале. При ошибке последующие действия остановятся.")) return;
        await Work(() =>
        {
            foreach (var item in items)
            {
                if (item.Kind == "temp")
                {
                    var file = new System.IO.FileInfo(item.Target);
                    if (!file.Exists || file.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-7)) throw new IOException("Временный файл изменился после просмотра; просканируйте папку повторно.");
                }
                changes.Apply(item.Kind, item.Target, item.After);
                Application.Current.Dispatcher.Invoke(() => Pending.Remove(item));
            }
        });
        // Do not replace an operation failure with a successful refresh message.
        var operationStatus = Status;
        await Refresh();
        if (operationStatus.StartsWith("Ошибка:")) Status = operationStatus;
    }
    private async Task RestoreAll()
    {
        if (!Confirm("Вернуть исходные значения всех изменений в обратном порядке? Откат остановится при конфликте с внешними изменениями.")) return;
        await Work(() => { foreach (var record in journal.Read().Reverse().Where(r => r.Status != "Восстановлено")) changes.Restore(record); });
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
