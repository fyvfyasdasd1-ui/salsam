using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Salsam.Core;

namespace Salsam.App;

public record PowerPlan(string Id, string Name) { public override string ToString() => Name; }
public record StartupEntry(string Name, string Command, string Publisher, string Purpose)
{
    public override string ToString() => $"{Name}\nИздатель: {Publisher}\nНазначение: {Purpose}\n{Command}";
}
public record TempEntry(string Path, long Bytes, DateTime Modified)
{
    public override string ToString() => $"{System.IO.Path.GetFileName(Path)} · {Bytes / 1048576.0:F2} МБ\n{Path}";
}
public record RegistryValue(string Text, int Kind);

public sealed class WindowsBackend : ISettingBackend
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Salsam");
    public static string Run(string executable, params string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var powershell = executable.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase);
        var systemExecutable = powershell
            ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")
            : executable.Equals("powercfg.exe", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(Environment.SystemDirectory, "powercfg.exe")
                : throw new NotSupportedException("Неизвестная системная утилита.");
        var encoding = powershell ? Encoding.UTF8 : Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        var info = new ProcessStartInfo(systemExecutable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = encoding, StandardErrorEncoding = encoding };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Не удалось запустить системную утилиту.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(20000)) { process.Kill(true); throw new TimeoutException("Системная утилита не ответила за 20 секунд."); }
        Task.WaitAll(output, error);
        if (process.ExitCode != 0) throw new IOException(error.Result + output.Result);
        return output.Result.Trim();
    }
    public static List<PowerPlan> Plans() => Run("powercfg.exe", "/list").Split('\n')
        .Select(line => Regex.Match(line, @"([a-fA-F0-9-]{36})\s+\((.+)\)"))
        .Where(match => match.Success).Select(match => new PowerPlan(match.Groups[1].Value.ToLowerInvariant(), match.Groups[2].Value)).ToList();
    public string Read(string kind, string target)
    {
        if (kind == "power") return Regex.Match(Run("powercfg.exe", "/getactivescheme"), @"[a-fA-F0-9-]{36}").Value.ToLowerInvariant();
        if (kind == "startup")
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            var value = key?.GetValue(target, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value == null) return "null";
            if (value is not string text) throw new NotSupportedException("Поддерживаются только строковые записи автозагрузки.");
            return JsonSerializer.Serialize(new RegistryValue(text, (int)key!.GetValueKind(target)));
        }
        if (kind == "temp")
        {
            var archive = Archive(target);
            if (File.Exists(target) && File.Exists(archive)) throw new IOException("Конфликт: файл есть и в исходной папке, и в карантине.");
            return File.Exists(target) ? "original" : File.Exists(archive) ? "quarantine" : "missing";
        }
        throw new NotSupportedException(kind);
    }
    private static string Archive(string target)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(target)));
        return Path.Combine(Data, "Quarantine", hash);
    }
    public void Write(string kind, string target, string value)
    {
        if (kind == "power")
        {
            if (!Guid.TryParse(value, out _) || !Plans().Any(p => p.Id == value)) throw new InvalidOperationException("Схема питания недоступна.");
            Run("powercfg.exe", "/setactive", value); return;
        }
        if (kind == "startup")
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value == "null") key.DeleteValue(target, false);
            else { var original = JsonSerializer.Deserialize<RegistryValue>(value)!; key.SetValue(target, original.Text, (RegistryValueKind)original.Kind); }
            return;
        }
        if (kind == "temp")
        {
            var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Временная папка является ссылкой.");
            var file = Path.GetFullPath(target);
            if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(file) + Path.DirectorySeparatorChar != root)
                throw new IOException("Разрешены только файлы непосредственно в пользовательской временной папке.");
            var source = value == "quarantine" ? target : Archive(target);
            var destination = value == "quarantine" ? Archive(target) : target;
            if (value is not ("original" or "quarantine")) throw new IOException("Неверное состояние файла.");
            if (File.Exists(destination)) { if (!File.Exists(source)) return; throw new IOException("Нельзя перезаписать существующий файл."); }
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("Ссылки не обрабатываются.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(source, destination); return;
        }
        throw new NotSupportedException(kind);
    }
    public static List<StartupEntry> Startup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return (key?.GetValueNames() ?? []).Select(name =>
        {
            var command = key!.GetValue(name)?.ToString() ?? "";
            var match = Regex.Match(command, "^\\s*(?:\"([^\"]+)\"|(.+?\\.exe)(?:\\s|$))", RegexOptions.IgnoreCase);
            var executable = Environment.ExpandEnvironmentVariables(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
            string publisher = "Не определён", purpose = "Не определено — проверьте программу перед отключением";
            try { if (File.Exists(executable)) { var version = FileVersionInfo.GetVersionInfo(executable); publisher = version.CompanyName ?? publisher; purpose = version.FileDescription ?? purpose; } } catch (Exception) { }
            return new StartupEntry(name, command, publisher + " (метаданные файла, не проверка подписи)", purpose);
        }).ToList();
    }
    public static List<TempEntry> PreviewTemps()
    {
        var root = new DirectoryInfo(Path.GetTempPath());
        if ((root.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Временная папка является ссылкой; очистка недоступна.");
        var result = new List<TempEntry>();
        foreach (var file in root.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) == 0 && file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7))
                    result.Add(new(file.FullName, file.Length, file.LastWriteTimeUtc));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }
    public static string Hardware() => Run("powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
        "[Console]::OutputEncoding=[Text.UTF8Encoding]::new(); $ErrorActionPreference='Stop'; 'ПРОЦЕССОР'; Get-CimInstance Win32_Processor | Select-Object Name | Format-List | Out-String; 'ВИДЕОКАРТА И ДРАЙВЕР'; Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion | Format-List | Out-String; 'ПАМЯТЬ'; Get-CimInstance Win32_ComputerSystem | Select-Object @{n='RAM_GB';e={[math]::Round($_.TotalPhysicalMemory/1GB,1)}},PCSystemType | Format-List | Out-String; 'ДИСКИ'; Get-CimInstance Win32_DiskDrive | Select-Object Model,@{n='GB';e={[math]::Round($_.Size/1GB)}} | Format-Table | Out-String; 'WINDOWS'; Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber | Format-List | Out-String");
    public static string RestorePoints() => Run("powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
        "[Console]::OutputEncoding=[Text.UTF8Encoding]::new(); $ErrorActionPreference='Stop'; $p=Get-ComputerRestorePoint; if($p){$p | Select-Object -Last 3 SequenceNumber,Description,CreationTime | Format-Table | Out-String}else{'Точки восстановления отсутствуют.'}");
    public static void OpenSettings(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
}

public sealed class MonitorService
{
    [StructLayout(LayoutKind.Sequential)] private struct Memory { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, Extended; }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref Memory memory);
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    private long previousIdle, previousTotal;
    public (double? Cpu, double Ram, double UsedGb, double TotalGb) Sample()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) throw new IOException("Счётчик CPU недоступен.");
        var total = kernel + user;
        double? cpu = previousTotal == 0 || total == previousTotal ? null : Math.Clamp(100.0 * (1 - (double)(idle - previousIdle) / (total - previousTotal)), 0, 100);
        previousTotal = total; previousIdle = idle;
        var memory = new Memory { Length = (uint)Marshal.SizeOf<Memory>() };
        if (!GlobalMemoryStatusEx(ref memory)) throw new IOException("Счётчик памяти недоступен.");
        return (cpu, memory.Load, (memory.TotalPhysical - memory.AvailablePhysical) / 1073741824.0, memory.TotalPhysical / 1073741824.0);
    }
}
