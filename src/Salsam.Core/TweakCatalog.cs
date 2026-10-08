using System.Globalization;
using System.Text.Json;

namespace Salsam.Core;

public enum TweakValueType { Boolean, MouseAcceleration, Integer, PowerScheme }

public sealed record TweakDefinition(string Id, string Kind, string Target, string Title,
    string Category, string Description, string Effect, string Tradeoff, string DesiredValue,
    TweakValueType ValueType = TweakValueType.Boolean, string Restart = "Без перезагрузки", string Unit = "");

public sealed record TweakState(string Label, bool AlreadyConfigured, bool? Enabled);

public static class TweakStates
{
    public static TweakState Evaluate(TweakDefinition definition, string actual)
    {
        ArgumentNullException.ThrowIfNull(definition);
        switch (definition.ValueType)
        {
            case TweakValueType.Boolean:
                if (actual is not ("true" or "false")) return new("Состояние неизвестно", false, null);
                var enabled = actual == "true";
                return new(enabled ? "Включено" : "Уже выключено",
                    Equivalent(definition.Kind, definition.Target, actual, definition.DesiredValue), enabled);
            case TweakValueType.MouseAcceleration:
                var mode = MouseVector(actual)[2];
                return new(mode == 0 ? "Уже выключено" : "Включено",
                    Equivalent(definition.Kind, definition.Target, actual, definition.DesiredValue), mode != 0);
            case TweakValueType.Integer:
                var value = ReadInteger(actual);
                var desired = ReadInteger(definition.DesiredValue);
                if (definition.Kind == "input")
                {
                    var valid = definition.Target switch
                    {
                        "keyboard-delay" => value is >= 0 and <= 3 && desired is >= 0 and <= 3,
                        "keyboard-speed" => value is >= 0 and <= 31 && desired is >= 0 and <= 31,
                        "mouse-trails" => value is >= 0 and <= 16 && (desired == 0 || desired is >= 2 and <= 16),
                        _ => false
                    };
                    if (!valid) throw new FormatException("Числовая настройка находится вне поддерживаемого диапазона.");
                }
                var configured = definition.Target == "mouse-trails"
                    ? Equivalent(definition.Kind, definition.Target, actual, definition.DesiredValue) : value == desired;
                if (definition.Target == "mouse-trails")
                    return new(value <= 1 ? "Уже выключено" : $"Включено · {value}{Unit(definition.Unit)}", configured, value > 1);
                if (value == 0 && definition.Kind == "power-value" &&
                    definition.Target.Split('/').ElementAtOrDefault(1) != "54533251-82be-4824-96c1-47b60b740d00")
                    return new("Уже выключено", configured, false);
                return new(configured ? "Уже настроено" : $"Сейчас: {value}{Unit(definition.Unit)}", configured, null);
            case TweakValueType.PowerScheme:
                if (!Guid.TryParse(actual, out var currentScheme) || !Guid.TryParse(definition.DesiredValue, out var desiredScheme))
                    return new("Схема недоступна", false, null);
                var chosen = currentScheme == desiredScheme;
                return new(chosen ? "Уже выбрано" : "Другая схема питания", chosen, null);
            default:
                throw new ArgumentOutOfRangeException(nameof(definition), "Неизвестный тип настройки.");
        }
    }

    public static bool Equivalent(string kind, string target, string actual, string desired)
    {
        if (kind == "input" && target == "mouse-acceleration")
            return (MouseVector(actual)[2] == 0) == (MouseVector(desired)[2] == 0);
        if (kind == "input" && target == "mouse-trails")
        {
            var current = ReadInteger(actual);
            var expected = ReadInteger(desired);
            if (current > 16 || expected > 16) throw new FormatException("Некорректная длина следа указателя мыши.");
            return current <= 1 && expected <= 1 || current == expected;
        }
        return string.Equals(actual, desired, StringComparison.Ordinal);
    }

    internal static int[] MouseVector(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new FormatException("Некорректное состояние ускорения мыши.");
        try
        {
            var vector = JsonSerializer.Deserialize<int[]>(value);
            if (vector is null || vector.Length != 3 || vector[0] < 0 || vector[1] < 0 || vector[2] is < 0 or > 2)
                throw new FormatException("Некорректное состояние ускорения мыши.");
            return vector;
        }
        catch (JsonException ex) { throw new FormatException("Некорректное состояние ускорения мыши.", ex); }
    }

    private static long ReadInteger(string value) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number is >= 0 and <= uint.MaxValue
        ? number : throw new FormatException("Не удалось прочитать числовую настройку.");
    private static string Unit(string unit) => string.IsNullOrWhiteSpace(unit) ? "" : " " + unit;
}

public static class TweakCatalog
{
    public static IReadOnlyList<TweakDefinition> Definitions { get; } = Array.AsReadOnly(new TweakDefinition[]
    {
        new("menu-animation", "visual", "menu-animation", "Анимация меню", "Интерфейс",
            "Плавное раскрытие системных меню.", "Меню появляются сразу; эффект относится к интерфейсу Windows.", "Меню открываются без перехода.", "false"),
        new("tooltip-animation", "visual", "tooltip-animation", "Анимация подсказок", "Интерфейс",
            "Переход при появлении всплывающих подсказок.", "Подсказки отображаются сразу.", "Переход становится резким.", "false"),
        new("client-area-animation", "visual", "client-area-animation", "Анимация элементов окон", "Интерфейс",
            "Анимации элементов приложений, которые используют системную настройку.", "Меньше движения в поддерживающих настройку программах.", "Некоторые переходы и визуальные подсказки исчезают.", "false"),
        new("minimize-animation", "visual", "minimize-animation", "Анимация сворачивания", "Интерфейс",
            "Переход при сворачивании и разворачивании окон.", "Окна меняют состояние без анимационного перехода.", "Исчезает визуальная связь окна с панелью задач.", "false"),
        new("menu-fade", "visual", "menu-fade", "Затухание меню", "Интерфейс",
            "Выбирает затухание меню вместо скольжения, когда анимация меню включена.", "При включённой анимации применяется другой вид перехода.", "Само отключение затухания не отключает анимацию меню и не измеряет FPS.", "false"),
        new("tooltip-fade", "visual", "tooltip-fade", "Затухание подсказок", "Интерфейс",
            "Выбирает затухание вместо скольжения для анимированных подсказок.", "Меняет вид перехода при включённой анимации подсказок.", "Чтобы убрать переход полностью, отключите анимацию подсказок отдельно.", "false"),
        new("combo-animation", "visual", "combo-animation", "Анимация выпадающих списков", "Интерфейс",
            "Раскрытие системных списков с анимацией.", "Списки раскрываются сразу.", "Изменение касается только программ, использующих системные списки.", "false"),
        new("listbox-smooth-scrolling", "visual", "listbox-smooth-scrolling", "Плавная прокрутка списков", "Интерфейс",
            "Плавная прокрутка стандартных элементов списка Windows.", "Прокрутка проходит без перехода в поддерживающих настройку списках.", "Прокрутка воспринимается более резкой.", "false"),
        new("selection-fade", "visual", "selection-fade", "Затухание выделения", "Интерфейс",
            "Затухание подсветки после выбора пункта меню.", "Меньше визуальных переходов в меню.", "Исчезает краткая подсветка выбранного пункта.", "false"),
        new("cursor-shadow", "visual", "cursor-shadow", "Тень указателя", "Мышь",
            "Системная тень под указателем мыши.", "Убирает декоративный эффект указателя.", "На светлом фоне указатель может быть менее заметен.", "false"),
        new("drop-shadow", "visual", "drop-shadow", "Тени системных меню", "Интерфейс",
            "Тени поддерживающих настройку системных меню и окон.", "Убирает декоративные тени в поддерживаемых элементах.", "Границы элементов могут быть менее заметны; тени современных приложений независимы.", "false"),
        new("full-window-drag", "visual", "full-window-drag", "Содержимое окна при перетаскивании", "Интерфейс",
            "Перерисовка содержимого окна во время перемещения.", "При отключении перемещается контур; меньше перерисовок во время перетаскивания.", "Содержимое окна видно только после завершения перемещения.", "false"),
        new("mouse-vanish", "visual", "mouse-vanish", "Скрытие указателя при вводе", "Мышь",
            "Автоматически скрывает указатель во время набора текста.", "При отключении указатель остаётся видимым.", "Указатель может перекрывать текст; прирост FPS не предполагается.", "false"),
        new("mouse-sonar", "visual", "mouse-sonar", "Поиск указателя по Ctrl", "Мышь",
            "Показывает положение указателя при нажатии клавиши Ctrl.", "Помогает найти указатель на большом или нескольких экранах.", "Нажатие Ctrl сопровождается дополнительной визуальной подсказкой.", "true"),
        new("mouse-clicklock", "visual", "mouse-clicklock", "Залипание кнопки мыши", "Мышь",
            "Позволяет удерживать выделение или перетаскивание без постоянного удержания кнопки.", "При отключении используется обычное удержание кнопки.", "Функция может быть нужна для доступности; отключайте только при необходимости.", "false"),
        new("keyboard-cues", "visual", "keyboard-cues", "Подсказки клавиш меню", "Клавиатура",
            "Постоянно подчёркивает клавиши доступа к пунктам меню.", "Проще пользоваться меню с клавиатуры.", "Подчёркивания всегда видны в поддерживающих настройку меню.", "true"),
        new("hot-tracking", "visual", "hot-tracking", "Подсветка элементов при наведении", "Интерфейс",
            "Системное отслеживание и подсветка активного элемента под указателем.", "Убирает часть визуальной реакции на движение указателя.", "Менее заметно, какой элемент находится под указателем.", "false"),
        new("mouse-acceleration", "input", "mouse-acceleration", "Ускорение указателя мыши", "Мышь",
            "Изменяет перемещение указателя в зависимости от скорости движения мыши.", "При отключении перемещение указателя Windows зависит от движения без ускорения.", "Потребуется привыкнуть к движению; игры с Raw Input используют собственный ввод.", "[0,0,0]", TweakValueType.MouseAcceleration),
        new("mouse-trails", "input", "mouse-trails", "След указателя мыши", "Мышь",
            "Рисует несколько копий указателя за его движением.", "При отключении остаётся один указатель.", "След может помогать видеть движение указателя на отдельных экранах.", "0", TweakValueType.Integer),
        new("keyboard-delay", "input", "keyboard-delay", "Задержка повтора клавиш", "Клавиатура",
            "Пауза перед повтором символа при удержании клавиши: 0 — самая короткая, 3 — самая длинная.", "Удерживаемая клавиша раньше начинает повторяться в поддерживающих настройку приложениях.", "Легче случайно ввести повторные символы; задержка первого нажатия не изменяется.", "0", TweakValueType.Integer, Unit: "из 3"),
        new("keyboard-speed", "input", "keyboard-speed", "Скорость повтора клавиш", "Клавиатура",
            "Частота повторных символов при удержании клавиши: от 0 до 31.", "Повтор символов становится быстрее.", "Легче случайно ввести лишние символы; игры могут обрабатывать повтор независимо.", "31", TweakValueType.Integer, Unit: "из 31"),
        new("show-extensions", "shell", "show-extensions", "Расширения файлов", "Проводник",
            "Показывает расширение в полном имени файла.", "Проще отличать типы файлов и замечать неожиданные расширения.", "Имена становятся длиннее; меняйте расширение при переименовании осторожно.", "true", Restart: "Переоткройте папку Проводника"),
        new("show-hidden-files", "shell", "show-hidden-files", "Скрытые файлы", "Проводник",
            "Показывает файлы с атрибутом «Скрытый»; отображение защищённых системных файлов настраивается отдельно.", "При отключении в папке меньше скрытых служебных файлов.", "Скрытые пользовательские файлы не видны до повторного включения настройки.", "false", Restart: "Переоткройте папку Проводника"),
        new("info-tips", "shell", "info-tips", "Подсказки Проводника", "Проводник",
            "Всплывающие сведения о файле или папке при наведении.", "При отключении всплывающие подсказки не перекрывают список файлов.", "Краткие сведения о файле придётся смотреть в свойствах или столбцах.", "false", Restart: "Переоткройте папку Проводника"),
        new("thumbnails", "shell", "thumbnails", "Миниатюры файлов", "Проводник",
            "Предпросмотр содержимого вместо обычных значков файлов.", "Отключение может уменьшить работу по созданию превью в больших папках с медиафайлами.", "Содержимое изображений и видео не видно по значку; скорость зависит от папки и оборудования.", "false", Restart: "Переоткройте папку Проводника"),
        new("selection-checkboxes", "shell", "selection-checkboxes", "Флажки выбора", "Проводник",
            "Флажки рядом с файлами для выбора нескольких элементов.", "Удобнее выбрать несколько файлов без удержания клавиш.", "Флажки занимают небольшую часть области списка.", "true", Restart: "Переоткройте папку Проводника"),
        new("status-bar", "shell", "status-bar", "Строка состояния", "Проводник",
            "Нижняя строка со сведениями о папке и выделенных файлах.", "Количество и свойства выделенных файлов видны сразу.", "Строка занимает часть высоты окна.", "true", Restart: "Переоткройте папку Проводника"),
        new("separate-process", "shell", "separate-process", "Отдельный процесс Проводника", "Проводник",
            "Предпочтение открывать окна папок в отдельном процессе Проводника.", "Может улучшить изоляцию окон; фактическое распределение процессов определяет Проводник.", "Возможно увеличение потребления RAM; применяется к новым окнам.", "true", Restart: "Переоткройте окна Проводника"),
        new("compressed-color", "shell", "compressed-color", "Цвет сжатых и зашифрованных файлов", "Проводник",
            "Выделяет цветом имена сжатых и зашифрованных файлов NTFS.", "Проще распознать эти атрибуты файла.", "Цвета имён меняются; действует для соответствующих файлов на NTFS.", "true", Restart: "Переоткройте папку Проводника"),
        new("high-performance", "power", "active", "Высокая производительность", "Электропитание",
            "Выбирает существующую схему Windows «Высокая производительность», если она доступна.", "Меняет правила энергосбережения согласно выбранной схеме; результат зависит от оборудования и нагрузки.", "Может увеличить нагрев, шум и расход батареи. FPS проверяйте отдельным измерением.", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", TweakValueType.PowerScheme)
    });
}
