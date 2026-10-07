using System.Text.Json;

namespace Salsam.App;

public sealed record HardwareInventory(string CpuName, string GpuName, string RamTotal, string WindowsName, string Details);

public static class InventoryService
{
    public static HardwareInventory Read()
    {
        var json = WindowsBackend.Run("powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
            "[Console]::OutputEncoding=[Text.UTF8Encoding]::new(); $ErrorActionPreference='Stop'; " +
            "$cpu=@(Get-CimInstance Win32_Processor); $gpu=@(Get-CimInstance Win32_VideoController); " +
            "$pc=Get-CimInstance Win32_ComputerSystem; $os=Get-CimInstance Win32_OperatingSystem; " +
            "$disks=@(Get-CimInstance Win32_DiskDrive); " +
            "[pscustomobject]@{Cpu=($cpu.Name -join ', '); Gpu=($gpu.Name -join ', '); " +
            "RamGB=[math]::Round($pc.TotalPhysicalMemory/1GB,1); OS=$os.Caption; Build=$os.BuildNumber; " +
            "Drivers=(@($gpu | ForEach-Object {$_.Name+': '+$_.DriverVersion}) -join [Environment]::NewLine); " +
            "Disks=(@($disks | ForEach-Object {$_.Model+' — '+[math]::Round($_.Size/1GB)+' GB'}) -join [Environment]::NewLine)} | ConvertTo-Json -Compress");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string Text(string name) => root.TryGetProperty(name, out var value) ? value.ToString() : "Недоступно";
        var cpu = Text("Cpu"); var gpu = Text("Gpu"); var ram = Text("RamGB") + " ГБ";
        var windows = Text("OS") + " · сборка " + Text("Build");
        var details = $"CPU: {cpu}\nGPU: {gpu}\nRAM: {ram}\n{windows}\n\nДрайверы GPU:\n{Text("Drivers")}\n\nДиски:\n{Text("Disks")}";
        return new(cpu, gpu, ram, windows, details);
    }
}
