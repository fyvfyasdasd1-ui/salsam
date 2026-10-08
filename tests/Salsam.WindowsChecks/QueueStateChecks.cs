using System.Runtime.ExceptionServices;
using Salsam.App;

public static class QueueStateChecks
{
    public static void Run()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Нужна Windows 10/11.");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { CheckQueueAndStates(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true, Name = "Salsam queue-state checks" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("Проверка состояний и очереди не завершилась за 30 секунд.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void CheckQueueAndStates()
    {
        // No Refresh, Apply, restoration, Application or dispatcher message loop:
        // only supplied observations and in-memory queue commands are exercised.
        var model = new MainViewModel();
        try
        {
            var menu = model.Optimizations.Single(item => item.Id == "menu-animation");
            menu.Update("false");
            Check(menu.State == "Уже выключено" && menu.IsAvailable && menu.IsAlreadyConfigured && !menu.CanSelect,
                "menu: already-disabled observation blocks redundant selection");
            Check(menu.QueueEnableCommand.CanExecute(null) && !menu.QueueDisableCommand.CanExecute(null),
                "menu: alternative enable remains available while redundant disable is blocked");

            menu.QueueEnableCommand.Execute(null);
            Check(model.Pending.Single().Kind == menu.Kind && model.Pending.Single().Target == menu.Target
                && model.Pending.Single().After == "true",
                "menu: explicit enable adds the opposite action to the queue");

            model.GamingCommand.Execute(null);
            Check(!model.Pending.Any(change => change.Kind == menu.Kind && change.Target == menu.Target),
                "gaming profile: already-disabled goal removes an earlier opposite enable action");

            menu.Update("true");
            menu.IsSelected = true;
            Check(menu.State == "Включено" && menu.CanSelect && !menu.IsAlreadyConfigured
                && menu.QueueDisableCommand.CanExecute(null),
                "menu: enabled observation allows the disable recommendation");
            menu.QueueDisableCommand.Execute(null);
            Check(model.Pending.Single().Target == menu.Target && model.Pending.Single().After == "false",
                "menu: enabled observation queues disable without changing Windows");

            menu.Update("false");
            Check(menu.State == "Уже выключено" && menu.IsAlreadyConfigured && !menu.CanSelect && !menu.IsSelected,
                "menu: later disabled observation clears selection and blocks repeat application");
            model.GamingCommand.Execute(null);
            Check(!model.Pending.Any(change => change.Kind == menu.Kind && change.Target == menu.Target),
                "gaming profile: redundant queued disable is also removed after the observation changes");

            var extensions = model.Optimizations.Single(item => item.Id == "show-extensions");
            extensions.Update("false");
            Check(extensions.State == "Уже выключено" && extensions.CanSelect && !extensions.IsAlreadyConfigured,
                "extensions: actual disabled state stays distinct from the enable recommendation");
            Check(extensions.QueueEnableCommand.CanExecute(null) && !extensions.QueueDisableCommand.CanExecute(null),
                "extensions: enabled goal does not relabel the actual disabled state");
            extensions.IsSelected = true;
            extensions.Update("true");
            Check(extensions.State == "Включено" && extensions.IsAlreadyConfigured && !extensions.CanSelect && !extensions.IsSelected,
                "extensions: observed enable satisfies the goal and clears selection");

            extensions.Update(null, "Наблюдение недоступно");
            Check(!extensions.IsAvailable && !extensions.IsAlreadyConfigured && !extensions.CanSelect
                && extensions.State.StartsWith("Недоступно:", StringComparison.Ordinal)
                && !extensions.QueueEnableCommand.CanExecute(null) && !extensions.QueueDisableCommand.CanExecute(null),
                "unavailable observation: no claimed configuration or selectable toggle action");
        }
        finally { model.Stop(); }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine("PASS " + name);
    }
}
