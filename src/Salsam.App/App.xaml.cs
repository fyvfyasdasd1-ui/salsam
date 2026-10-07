namespace Salsam.App;
public partial class App : System.Windows.Application
{
    private System.Threading.Mutex? instance;
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            ReportFailure(args.Exception);
            Shutdown(1);
        };
        try
        {
            instance = new System.Threading.Mutex(true, "Local\\Salsam-" + Environment.UserName, out var created);
            if (!created) { System.Windows.MessageBox.Show("Salsam уже запущен. Откройте его окно на панели задач."); Shutdown(); return; }
            base.OnStartup(e);
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        catch (Exception ex) { ReportFailure(ex); Shutdown(1); }
    }
    private static void ReportFailure(Exception exception)
    {
        var details = "";
        try
        {
            System.IO.Directory.CreateDirectory(WindowsBackend.Data);
            var path = System.IO.Path.Combine(WindowsBackend.Data, "startup-error.log");
            System.IO.File.WriteAllText(path, DateTimeOffset.Now + "\n" + exception);
            details = "\n\nПодробности: " + path;
        }
        catch (Exception) { }
        System.Windows.MessageBox.Show("Не удалось продолжить работу Salsam.\n" + exception.GetBaseException().Message + details,
            "Salsam — ошибка", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
    }
    protected override void OnExit(System.Windows.ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
