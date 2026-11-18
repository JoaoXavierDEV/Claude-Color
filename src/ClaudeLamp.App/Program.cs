using ClaudeLamp.Core;

namespace ClaudeLamp.App;

internal static class Program
{
    public static string ExePath => Environment.ProcessPath!;

    [STAThread]
    private static int Main(string[] args)
    {
        // Modos sem interface, usados pelos hooks do Claude Code.
        if (args.Length > 0)
        {
            switch (args[0].ToLowerInvariant())
            {
                case "hook":
                    var input = HookHandler.ReadStdinAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                    HookHandler.Handle(input, ExePath);
                    return 0; // nunca falha: o Claude Code não pode ser atrapalhado

                case "run-signal" when args.Length >= 3:
                    try
                    {
                        SignalRunner.RunAsync(Storage.LoadConfig(), args[1], long.Parse(args[2])).GetAwaiter().GetResult();
                    }
                    catch (Exception ex) { Storage.Log($"run-signal falhou: {ex}"); }
                    return 0;

                case "install-hooks":
                    new HooksInstaller().Install(ExePath);
                    return 0;

                case "uninstall-hooks":
                    new HooksInstaller().Uninstall();
                    return 0;

                // Executados numa instância elevada (UAC) a pedido da aba Sistema.
                case "install-autostart":
                    return Elevation.IsElevated && Elevation.SetAutostartTask(true) ? 0 : 1;

                case "uninstall-autostart":
                    return Elevation.IsElevated && Elevation.SetAutostartTask(false) ? 0 : 1;
            }
        }

        // "--after <pid>": reinício como admin; espera a instância anterior fechar.
        var afterIdx = Array.IndexOf(args, "--after");
        if (afterIdx >= 0 && afterIdx + 1 < args.Length && int.TryParse(args[afterIdx + 1], out var oldPid))
        {
            try { System.Diagnostics.Process.GetProcessById(oldPid).WaitForExit(15000); }
            catch (ArgumentException) { /* já fechou */ }
        }

        Mutex mutex;
        EventWaitHandle showSignal;
        bool first;
        try
        {
            mutex = new Mutex(true, @"Local\ClaudeLamp.App", out first);
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ClaudeLamp.App.Show");
        }
        catch (UnauthorizedAccessException)
        {
            // Já existe uma instância rodando como administrador; esta (sem admin) não consegue abrir os objetos dela.
            MessageBox.Show("O Claude Lamp já está aberto como administrador.\nUse o ícone na bandeja.", "Claude Lamp",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        using var _m = mutex;
        using var _s = showSignal;
        if (!first)
        {
            showSignal.Set(); // já está rodando: pede para a instância aberta mostrar a janela
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Storage.Log($"ui: {e.Exception}");
        Application.Run(new TrayContext(showSignal, openSettings: !args.Contains("--tray")));
        return 0;
    }
}
