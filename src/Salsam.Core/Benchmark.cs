using System.Globalization;

namespace Salsam.Core;

public sealed record FrameReport(int Count, double AverageFps, double OnePercentLow, double[] Milliseconds)
{
    public static FrameReport Parse(string csv)
    {
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2) throw new FormatException("CSV должен содержать заголовок и измерения.");
        var delimiter = lines[0].Contains(';') ? ';' : ',';
        var headers = lines[0].TrimStart('\uFEFF').Split(delimiter).Select(s => s.Trim().Trim('"')).ToArray();
        var column = Array.FindIndex(headers, h => h is "MsBetweenPresents" or "FrameTimeMs");
        if (column < 0) throw new FormatException("Нужен столбец MsBetweenPresents или FrameTimeMs (миллисекунды).");
        var frames = new List<double>();
        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split(delimiter);
            if (cells.Length <= column || !double.TryParse(cells[column].Trim().Trim('"'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var ms) || !double.IsFinite(ms) || ms <= 0)
                throw new FormatException($"Некорректное время кадра в строке {frames.Count + 2}.");
            frames.Add(ms);
        }
        if (frames.Count < 100) throw new FormatException("Для сравнения нужно не менее 100 кадров.");
        var slowest = frames.OrderDescending().Take((int)Math.Ceiling(frames.Count * .01)).Average();
        return new(frames.Count, 1000 / frames.Average(), 1000 / slowest, frames.ToArray());
    }
}
