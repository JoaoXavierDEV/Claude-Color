using ClaudeColor.Core;

namespace ClaudeColor.App;

/// <summary>
/// "ClaudeColor.exe uninstall [--purge]": tira o Claude Color do sistema. Remove os hooks (com backup do
/// settings.json), a tarefa agendada, a entrada de inicialização e o registro das notificações; com --purge,
/// apaga também %APPDATA%\ClaudeColor. O exe não se apaga sozinho: a pasta dele é aberta no fim.
/// </summary>
internal static class Uninstaller
{
    public static int Run(bool purge, bool quiet = false)
    {
        // App aberto: pede para sair (ele devolve as luzes antes) e espera.
        if (SignalPipe.TrySendLine(SignalPipe.QuitCommand, 1000))
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < until &&
                   System.Diagnostics.Process.GetProcessesByName("ClaudeColor").Any(p => p.Id != Environment.ProcessId))
                Thread.Sleep(250);
        }

        var done = new List<string>();
        var failed = new List<string>();
        void Step(string what, Action action)
        {
            try { action(); done.Add(what); }
            catch (Exception ex) { failed.Add($"{what}: {ex.Message}"); }
        }

        Step("Hooks removidos do ~/.claude/settings.json (com backup)", () => new HooksInstaller().Uninstall());
        if (Elevation.AutostartTaskExists())
            Step("Tarefa agendada removida", () =>
            {
                if (!Elevation.SetAutostartTask(false)) throw new InvalidOperationException("UAC cancelado ou sem permissão");
            });
        Step("Inicialização com o Windows removida", Elevation.RemoveRunEntry);
        Step("Registro das notificações removido", Notifier.Unregister);
        if (purge)
            Step($"Configuração, perfis e log apagados ({Storage.Directory})", () =>
            {
                if (Directory.Exists(Storage.Directory)) Directory.Delete(Storage.Directory, recursive: true);
            });
        else Storage.Log($"desinstalado: {string.Join("; ", done)}");

        if (!quiet)
        {
            var text = "Claude Color desinstalado.\n\n" + string.Join("\n", done.Select(d => "✓ " + d)) +
                       (failed.Count > 0 ? "\n\nNão deu:\n" + string.Join("\n", failed.Select(f => "✗ " + f)) : "") +
                       $"\n\nPara terminar, apague a pasta do app (vai abrir agora):\n{AppContext.BaseDirectory}";
            MessageBox.Show(text, "Claude Color", MessageBoxButtons.OK, failed.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            try { System.Diagnostics.Process.Start("explorer.exe", $"\"{AppContext.BaseDirectory.TrimEnd('\\')}\""); } catch { }
        }
        return failed.Count == 0 ? 0 : 1;
    }
}
