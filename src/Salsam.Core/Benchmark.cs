using System.Globalization;
using Microsoft.VisualBasic.FileIO;

namespace Salsam.Core;

public sealed record FrameReport(int Count, double AverageFps, double OnePercentLow, double[] Milliseconds)
{
    public static FrameReport Parse(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) throw new FormatException("CSV должен содержать заголовок и измерения.");
        var input = csv.TrimStart('\uFEFF', '\r', '\n', ' ', '\t');
        using var parser = new TextFieldParser(new StringReader(input))
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = true
        };
        parser.SetDelimiters(DetectDelimiter(input));
        var headers = ReadFields(parser) ?? throw new FormatException("CSV должен содержать заголовок и измерения.");
        if (headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Length)
            throw new FormatException("Заголовок CSV содержит повторяющиеся столбцы.");
        var column = Array.FindIndex(headers, h => h is "MsBetweenPresents" or "FrameTimeMs");
        if (column < 0) throw new FormatException("Нужен столбец MsBetweenPresents или FrameTimeMs (миллисекунды).");
        var processColumn = Array.FindIndex(headers, h => h.Equals("ProcessID", StringComparison.OrdinalIgnoreCase));
        uint? processId = null;
        var frames = new List<double>();
        while (!parser.EndOfData)
        {
            var lineNumber = parser.LineNumber;
            var cells = ReadFields(parser);
            if (cells is null) break;
            if (cells.Length != headers.Length)
                throw new FormatException($"Количество столбцов не совпадает с заголовком в строке {lineNumber}.");
            if (!double.TryParse(cells[column], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var ms) || !double.IsFinite(ms) || ms <= 0)
                throw new FormatException($"Некорректное время кадра в строке {lineNumber}.");
            if (processColumn >= 0)
            {
                if (!uint.TryParse(cells[processColumn], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                    throw new FormatException($"Некорректный ProcessID в строке {lineNumber}.");
                if (processId.HasValue && processId.Value != id)
                    throw new FormatException("CSV содержит кадры разных процессов. Экспортируйте измерения отдельно для процесса игры.");
                processId = id;
            }
            frames.Add(ms);
        }
        if (frames.Count < 100) throw new FormatException("Для сравнения нужно не менее 100 кадров.");
        var slowest = Mean(frames.OrderDescending().Take((int)Math.Ceiling(frames.Count * .01)));
        var averageFps = 1000 / Mean(frames);
        var onePercentLow = 1000 / slowest;
        if (!double.IsFinite(averageFps) || !double.IsFinite(onePercentLow))
            throw new FormatException("Время кадра слишком мало для вычисления конечного значения FPS.");
        return new(frames.Count, averageFps, onePercentLow, frames.ToArray());
    }

    private static string[]? ReadFields(TextFieldParser parser)
    {
        try { return parser.ReadFields(); }
        catch (MalformedLineException ex)
        {
            throw new FormatException($"Некорректная структура CSV в строке {ex.LineNumber}.", ex);
        }
    }

    private static string DetectDelimiter(string input)
    {
        var commas = 0;
        var semicolons = 0;
        var quoted = false;
        for (var i = 0; i < input.Length; i++)
        {
            var character = input[i];
            if (character == '"')
            {
                if (quoted && i + 1 < input.Length && input[i + 1] == '"') i++;
                else quoted = !quoted;
            }
            else if (!quoted)
            {
                if (character is '\r' or '\n') break;
                if (character == ',') commas++;
                if (character == ';') semicolons++;
            }
        }
        return semicolons > commas ? ";" : ",";
    }

    // Positive samples make this incremental mean stable even when their sum would overflow.
    private static double Mean(IEnumerable<double> samples)
    {
        var mean = 0d;
        var count = 0;
        foreach (var sample in samples) mean += (sample - mean) / ++count;
        return mean;
    }
}
