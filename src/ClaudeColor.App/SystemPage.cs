using ClaudeColor.Core;

namespace ClaudeColor.App;

/// <summary>Hooks do Claude Code, administrador, inicialização com o Windows e arquivos.</summary>
internal sealed class SystemPage : Page
{
    private readonly HooksInstaller _hooks = new();
    private readonly Label _hooksStatus = Ui.InlineStatus();
    private readonly Label _status = Ui.Status();
    private readonly CheckBox _autostart = Ui.Check("Iniciar com o Windows, como administrador (Tarefa Agendada, sem UAC a cada login)");
    private readonly CheckBox _launchFromHook = Ui.Check("Abrir o Claude Color na bandeja quando o Claude Code mandar um sinal com o app fechado");
    private readonly Label _elevated = Ui.InlineStatus();
    private readonly Button _restartAdmin;
    private bool _loading;

    public override string NavTitle => "Sistema";

    public SystemPage() : base("Sistema", "Integração com o Claude Code e com o Windows.")
    {
        var hooks = Add(new Card("Hooks do Claude Code",
            "O Claude Code avisa o Claude Color por hooks em ~/.claude/settings.json: UserPromptSubmit, Stop, Notification, " +
            "PreToolUse (AskUserQuestion / ExitPlanMode) e PostToolUse. Um backup é criado antes de cada alteração, e hooks de outras ferramentas são preservados."));
        hooks.Add(_hooksStatus,
            Ui.Row(Ui.Button("Instalar / atualizar hooks", (_, _) => Act(() => _hooks.Install(Program.ExePath), "Hooks instalados"), primary: true),
                   Ui.Button("Remover hooks", (_, _) => Act(() => _hooks.Uninstall(), "Hooks removidos"))),
            hooks.Wrap(Ui.Hint($"Arquivo: {_hooks.SettingsPath}. Sessões do Claude Code já abertas podem precisar ser reiniciadas.")));

        var windows = Add(new Card("Windows"));
        _restartAdmin = Ui.Button("Reiniciar como administrador", (_, _) => RestartAsAdmin());
        windows.Add(Ui.Row(_elevated, _restartAdmin),
            windows.Wrap(Ui.Hint("Só é preciso para o MSI Mystic Light (e para o OpenRGB iniciado por aqui controlar memória e placa-mãe).")),
            _autostart, _launchFromHook);
        _autostart.CheckedChanged += async (_, _) => { if (!_loading) await SetAutostartAsync(_autostart.Checked); };
        _launchFromHook.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            var cfg = Storage.LoadConfig();
            cfg.LaunchFromHook = _launchFromHook.Checked;
            Storage.SaveConfig(cfg);
        };

        var files = Add(new Card("Arquivos"));
        files.Add(
            Ui.Row(
                Ui.Button("Abrir log", (_, _) => TrayContext.OpenPath(Storage.LogPath)),
                Ui.Button("Abrir config.json", (_, _) => { if (!File.Exists(Storage.ConfigPath)) Storage.SaveConfig(Storage.LoadConfig()); TrayContext.OpenPath(Storage.ConfigPath); }),
                Ui.Button("Abrir pasta do app", (_, _) => TrayContext.OpenPath(AppContext.BaseDirectory))),
            files.Wrap(Ui.Hint($"Executável: {Program.ExePath}")),
            files.Wrap(Ui.Hint($"Dados: {Storage.Directory}")),
            Ui.Hint($"Claude Color {Program.Version}"),
            _status);
    }

    public override void OnShown()
    {
        _loading = true;
        try
        {
            (_hooksStatus.Text, _hooksStatus.ForeColor) = _hooks.GetStatus(Program.ExePath) switch
            {
                HooksStatus.Installed => ("●  Instalados e apontando para este app", Ui.Ok),
                HooksStatus.Outdated => ("●  Desatualizados (apontam para outro executável): clique em Instalar / atualizar", Ui.Warn),
                _ => ("●  Não instalados", Ui.Error),
            };
            var admin = Elevation.IsElevated;
            Ui.SetStatus(_elevated, admin ? true : null, admin ? "Executando como administrador" : "Executando sem privilégios de administrador");
            _restartAdmin.Visible = !admin;
            _autostart.Checked = Elevation.AutostartTaskExists();
            _launchFromHook.Checked = Storage.LoadConfig().LaunchFromHook;
        }
        catch (Exception ex) { _status.ForeColor = Ui.Error; _status.Text = ex.Message; }
        finally { _loading = false; }
    }

    private void Act(Func<string?> action, string done)
    {
        try
        {
            var backup = action();
            _status.ForeColor = Ui.Ok;
            _status.Text = backup is null ? $"{done}." : $"{done}. Backup: {backup}";
        }
        catch (Exception ex)
        {
            Storage.Log($"hooks: {ex}");
            _status.ForeColor = Ui.Error;
            _status.Text = ex.Message;
        }
        OnShown();
    }

    private void RestartAsAdmin()
    {
        if (Elevation.RestartElevated()) TrayContext.Current?.ExitThread();
        else { _status.ForeColor = Ui.Error; _status.Text = "Reinício como administrador cancelado."; }
    }

    private async Task SetAutostartAsync(bool on)
    {
        _autostart.Enabled = false;
        try
        {
            if (!Elevation.SetAutostartTask(on))
            {
                _status.ForeColor = Ui.Error;
                _status.Text = "Não foi possível alterar a Tarefa Agendada (UAC cancelado?).";
            }
            else
            {
                if (on) Elevation.RemoveRunEntry();
                // Sem admin a criação roda numa instância elevada separada: espera ela terminar.
                for (int i = 0; i < 20 && Elevation.AutostartTaskExists() != on; i++) await Task.Delay(500);
                var done = Elevation.AutostartTaskExists() == on;
                _status.ForeColor = done ? Ui.Ok : Ui.Error;
                _status.Text = done
                    ? (on ? "Vai iniciar com o Windows como administrador." : "Não vai mais iniciar com o Windows.")
                    : "A Tarefa Agendada não mudou; veja o log.";
            }
        }
        finally
        {
            _autostart.Enabled = true;
            OnShown();
        }
    }
}
