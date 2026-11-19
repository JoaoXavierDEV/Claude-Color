using ClaudeColor.Core;

namespace ClaudeColor.App;

internal static class Program
{
    public static string ExePath => Environment.ProcessPath!;

    public static string Version => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "";

    [STAThread]
    private static int Main(string[] args)
    {
        Storage.MigrateFrom(Storage.LegacyDirectory); // 1.x (ClaudeLamp): copia config e estado uma vez

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

                // Fecha o app aberto devolvendo as luzes (antes de atualizar o exe). Espera até 20 s.
                case "--exit":
                {
                    if (!SignalPipe.TrySendLine(SignalPipe.QuitCommand, 1000)) return 0; // não estava aberto
                    var until = DateTime.UtcNow.AddSeconds(20);
                    while (DateTime.UtcNow < until &&
                           System.Diagnostics.Process.GetProcessesByName("ClaudeColor").Any(p => p.Id != Environment.ProcessId))
                        Thread.Sleep(250);
                    return 0;
                }

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

                // Gera um PNG de cada página da janela (documentação e revisão do layout).
                case "--screenshots" when args.Length >= 2:
                    ApplicationConfiguration.Initialize();
                    Ui.ApplyTheme(Storage.LoadConfig().Theme);
                    Screenshots.Save(args[1]);
                    return 0;
            }
        }

        // "--after <pid>": reinício como admin; espera a instância anterior fechar.
        var afterIdx = Array.IndexOf(args, "--after");
        if (afterIdx >= 0 && afterIdx + 1 < args.Length && int.TryParse(args[afterIdx + 1], out var oldPid))
        {
            try { System.Diagnostics.Process.GetProcessById(oldPid).WaitForExit(15000); }
            catch (ArgumentException) { /* já fechou */ }
        }

        // Aberto pelo hook sem admin, mas há a tarefa elevada: usa ela, para o Mystic Light funcionar.
        if (args.Contains("--from-hook") && !Elevation.IsElevated && Elevation.AutostartTaskExists() && Elevation.RunAutostartTask())
            return 0;

        Mutex mutex;
        EventWaitHandle showSignal;
        bool first;
        try
        {
            mutex = new Mutex(true, @"Local\ClaudeColor.App", out first);
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ClaudeColor.App.Show");
        }
        catch (UnauthorizedAccessException)
        {
            // Já existe uma instância rodando como administrador; esta (sem admin) não consegue abrir os objetos dela.
            MessageBox.Show("O Claude Color já está aberto como administrador.\nUse o ícone na bandeja.", "Claude Color",
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
        Ui.ApplyTheme(Storage.LoadConfig().Theme);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Storage.Log($"ui: {e.Exception}");
        Application.Run(new TrayContext(showSignal, openSettings: !args.Contains("--tray")));
        return 0;
    }
}
