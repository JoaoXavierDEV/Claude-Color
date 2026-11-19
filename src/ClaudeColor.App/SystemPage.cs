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
    private readonly ComboBox _theme = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private static readonly (string Key, string Label)[] Themes = [("system", "Seguir o Windows"), ("light", "Claro"), ("dark", "Escuro")];
    private readonly CheckBox _quietNotify = Ui.Check("Não mostrar notificações");
    private readonly CheckBox _quietFlash = Ui.Check("Não piscar o terminal na barra de tarefas");
    private readonly CheckBox _quietLights = Ui.Check("Não acender as luzes");
    private readonly Label _notifyStatus = Ui.InlineStatus(), _busyStatus = Ui.Label("");
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
        foreach (var (_, label) in Themes) _theme.Items.Add(label);
        var themeLabel = Ui.Label("Tema da janela");
        themeLabel.Margin = new Padding(0, 10, 8, 4);
        windows.Add(Ui.Row(themeLabel, _theme), windows.Wrap(Ui.Hint("Também dá para alternar pelo interruptor no fim do menu lateral ou pela bandeja.")));
        _theme.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _theme.SelectedIndex < 0) return;
            TrayContext.Current?.SetTheme(Themes[_theme.SelectedIndex].Key);
        };
        _autostart.CheckedChanged += async (_, _) => { if (!_loading) await SetAutostartAsync(_autostart.Checked); };
        _launchFromHook.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            var cfg = Storage.LoadConfig();
            cfg.LaunchFromHook = _launchFromHook.Checked;
            Storage.SaveConfig(cfg);
        };

        var notify = Add(new Card("Notificações e tela cheia",
            "Notificações do Windows e a barra de tarefas acompanham os sinais (escolha por evento na página Sinais). " +
            "Quando o Windows diz que você está ocupado (jogo ou vídeo em tela cheia, apresentação, horário silencioso):"));
        notify.Add(_quietNotify, _quietFlash, _quietLights, _busyStatus,
            Ui.Row(_notifyStatus,
                Ui.Button("Enviar notificação de teste", (_, _) =>
                {
                    Notifier.Show("Pergunta para você", "Em teste. Clique para voltar ao terminal.", "#FFC000", SessionInfo.None);
                    OnShown();
                }),
                Ui.Link("Configurações de notificação", "ms-settings:notifications")),
            notify.Wrap(Ui.Hint("O modo \"Não incomodar\" do Windows guarda as notificações na central sozinho, sem tocar som.")));
        foreach (var cb in new[] { _quietNotify, _quietFlash, _quietLights })
            cb.CheckedChanged += (_, _) =>
            {
                if (_loading) return;
                var cfg = Storage.LoadConfig();
                cfg.Quiet.SkipNotify = _quietNotify.Checked;
                cfg.Quiet.SkipFlash = _quietFlash.Checked;
                cfg.Quiet.SkipLights = _quietLights.Checked;
                Storage.SaveConfig(cfg);
            };

        var uninstall = Add(new Card("Desinstalar",
            "Remove os hooks do Claude Code (com backup do settings.json), a tarefa agendada, a inicialização com o Windows e o registro " +
            "das notificações, devolve as luzes e fecha o app. O exe não se apaga sozinho: a pasta dele é aberta no fim para você apagar."));
        var purge = Ui.Check("Apagar também configuração, perfis e log (" + Storage.Directory + ")");
        uninstall.Add(purge, Ui.Button("Desinstalar o Claude Color", (_, _) =>
        {
            if (MessageBox.Show(FindForm(), "Desinstalar o Claude Color?" + (purge.Checked ? "\n\nA configuração e os perfis serão apagados." : ""),
                    "Claude Color", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            // Um processo separado faz a remoção: ele pede para este app sair (devolvendo as luzes) e espera.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Program.ExePath, purge.Checked ? "uninstall --purge" : "uninstall")
            {
                UseShellExecute = true,
            });
        }));

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
            var cfg = Storage.LoadConfig();
            _launchFromHook.Checked = cfg.LaunchFromHook;
            _theme.SelectedIndex = Math.Max(0, Array.FindIndex(Themes, t => t.Key == cfg.Theme));
            _quietNotify.Checked = cfg.Quiet.SkipNotify;
            _quietFlash.Checked = cfg.Quiet.SkipFlash;
            _quietLights.Checked = cfg.Quiet.SkipLights;
            var (ok, text) = Notifier.Status();
            Ui.SetStatus(_notifyStatus, ok ? true : false, "Notificações: " + text);
            var busy = UserBusy.Current();
            _busyStatus.ForeColor = Ui.Muted;
            _busyStatus.Font = Ui.Small;
            _busyStatus.Text = UserBusy.IsBusy(busy) ? $"Agora: ocupado ({UserBusy.Describe(busy)})." : "Agora: disponível.";
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
