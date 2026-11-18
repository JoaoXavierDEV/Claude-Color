using ClaudeLamp.Core;
using Microsoft.Win32;

namespace ClaudeLamp.App;

/// <summary>Hooks do Claude Code, inicialização com o Windows e arquivos.</summary>
internal sealed class SystemTab : UserControl
{
    private readonly HooksInstaller _hooks = new();
    private readonly Label _hooksStatus = Ui.Label("", bold: true);
    private readonly Label _status = Ui.Label("");
    private readonly CheckBox _autostart = new() { Text = "Iniciar o Claude Lamp com o Windows, como administrador (Tarefa Agendada)", AutoSize = true };
    private readonly Label _elevated = Ui.Label("", bold: true);
    private readonly Button _restartAdmin;
    private bool _loading;

    public SystemTab()
    {
        var root = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };

        root.Controls.Add(Ui.Label("Hooks do Claude Code", bold: true));
        root.Controls.Add(Ui.Label($"Arquivo: {_hooks.SettingsPath}"));
        root.Controls.Add(_hooksStatus);
        root.Controls.Add(Ui.Row(
            Ui.Button("Instalar / atualizar hooks", (_, _) => Act(() => _hooks.Install(Program.ExePath), "Hooks instalados")),
            Ui.Button("Remover hooks", (_, _) => Act(() => _hooks.Uninstall(), "Hooks removidos"))));
        root.Controls.Add(Ui.Label("Os eventos usados: UserPromptSubmit, Stop, Notification e PreToolUse (AskUserQuestion / ExitPlanMode).\n" +
                                   "Um backup do settings.json é criado antes de cada alteração. Sessões já abertas podem precisar ser reiniciadas."));

        root.Controls.Add(new Label { Height = 10 });
        root.Controls.Add(Ui.Label("Windows", bold: true));
        _restartAdmin = Ui.Button("Reiniciar como administrador", (_, _) => RestartAsAdmin());
        root.Controls.Add(Ui.Row(_elevated, _restartAdmin));
        root.Controls.Add(Ui.Label("A placa de vídeo MSI (Mystic Light) só responde com o app rodando como administrador."));
        _autostart.CheckedChanged += async (_, _) => { if (!_loading) await SetAutostartAsync(_autostart.Checked); };
        root.Controls.Add(_autostart);

        root.Controls.Add(new Label { Height = 10 });
        root.Controls.Add(Ui.Label("Arquivos", bold: true));
        root.Controls.Add(Ui.Row(
            Ui.Button("Abrir log", (_, _) => TrayContext.OpenPath(Storage.LogPath)),
            Ui.Button("Abrir config.json", (_, _) => { if (!File.Exists(Storage.ConfigPath)) Storage.SaveConfig(Storage.LoadConfig()); TrayContext.OpenPath(Storage.ConfigPath); }),
            Ui.Button("Abrir pasta do app", (_, _) => TrayContext.OpenPath(AppContext.BaseDirectory))));
        root.Controls.Add(Ui.Label($"Executável: {Program.ExePath}"));
        root.Controls.Add(Ui.Label($"Versão {typeof(Program).Assembly.GetName().Version?.ToString(3)}"));
        root.Controls.Add(_status);
        Controls.Add(root);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) Refresh2();
    }

    private void Refresh2()
    {
        _loading = true;
        try
        {
            (_hooksStatus.Text, _hooksStatus.ForeColor) = _hooks.GetStatus(Program.ExePath) switch
            {
                HooksStatus.Installed => ("● Instalados e apontando para este app", Ui.Ok),
                HooksStatus.Outdated => ("● Desatualizados (apontam para outro executável) — clique em Instalar / atualizar", Color.DarkOrange),
                _ => ("● Não instalados", Ui.Error),
            };
            var admin = Elevation.IsElevated;
            _elevated.Text = admin ? "● Executando como administrador" : "● Executando sem privilégios de administrador";
            _elevated.ForeColor = admin ? Ui.Ok : Color.DarkOrange;
            _restartAdmin.Visible = !admin;
            _autostart.Checked = Elevation.AutostartTaskExists();
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
        Refresh2();
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
            Refresh2();
        }
    }
}
