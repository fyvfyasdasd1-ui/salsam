using Salsam.Core;

public static class BenchmarkChecks
{
    public static int Run()
    {
        var passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            Console.WriteLine("PASS " + name);
            passed++;
        }
        void Reject(string csv, string name, string? messageFragment = null)
        {
            try { FrameReport.Parse(csv); }
            catch (FormatException ex)
            {
                Check(messageFragment is null || ex.Message.Contains(messageFragment, StringComparison.Ordinal), name);
                return;
            }
            throw new Exception("Expected CSV rejection: " + name);
        }
        string Rows(string header, string row) => header + "\n" + string.Join('\n', Enumerable.Repeat(row, 100));
        var quoted = FrameReport.Parse(Rows("Application,ProcessID,MsBetweenPresents", "\"game, \"\"edition\"\".exe\",123,\"10\""));
        Check(quoted.Count == 100 && Math.Abs(quoted.AverageFps - 100) < .001, "PresentMon quoted comma and escaped quote fields parse correctly");
        var semicolon = FrameReport.Parse(Rows("\uFEFF\"Application;name\";ProcessId;FrameTimeMs", "\"game;edition.exe\";42;12.5"));
        Check(semicolon.Count == 100 && Math.Abs(semicolon.AverageFps - 80) < .001, "semicolon CSV and BOM retain invariant decimal frame times");
        var multiline = FrameReport.Parse(Rows("Application,FrameTimeMs", "\"game\nname\",10"));
        Check(multiline.Count == 100, "quoted multiline fields remain a single measured frame");
        Reject(Rows("Application,ProcessID,MsBetweenPresents", "game.exe,123,10") + "\ngame.exe,124,10", "mixed process export is rejected with a game export explanation", "отдельно для процесса игры");
        Reject(Rows("ProcessId,FrameTimeMs", "bad,10"), "invalid process ID is rejected");
        Reject(Rows("FrameTimeMs", "-1"), "negative frame time is rejected");
        Reject(Rows("FrameTimeMs", "0"), "zero frame time is rejected");
        Reject(Rows("FrameTimeMs", "NaN"), "nonfinite NaN frame time is rejected");
        Reject(Rows("FrameTimeMs", "Infinity"), "nonfinite infinity frame time is rejected");
        Reject(Rows("FrameTimeMs", "1e309"), "overflowing frame time is rejected");
        Reject(Rows("FrameTimeMs", "\"12,5\""), "locale comma decimal is not silently interpreted");
        Reject(Rows("Application,FrameTimeMs", "\"unfinished,10"), "malformed quoted fields are rejected");
        Reject(Rows("Application,FrameTimeMs", "game.exe,10,extra"), "unexpected columns are rejected");
        Reject(Rows("Application,FrameTimeMs", "game.exe"), "missing columns are rejected");
        Reject(Rows("FrameTimeMs,FrameTimeMs", "10,20"), "ambiguous repeated headers are rejected");
        Reject("FrameTimeMs\n" + string.Join('\n', Enumerable.Repeat("10", 99)), "benchmark retains minimum one hundred frames");
        var large = FrameReport.Parse(Rows("FrameTimeMs", "1e308"));
        Check(double.IsFinite(large.AverageFps) && large.AverageFps > 0, "positive finite sample mean does not overflow");
        return passed;
    }
}
