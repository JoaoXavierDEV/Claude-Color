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
            }
        }

        using var mutex = new Mutex(true, @"Local\ClaudeLamp.App", out bool first);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ClaudeLamp.App.Show");
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
