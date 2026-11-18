using ClaudeLamp.Core;
using Microsoft.Win32;

namespace ClaudeLamp.App;

/// <summary>Hooks do Claude Code, inicialização com o Windows e arquivos.</summary>
internal sealed class SystemTab : UserControl
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ClaudeLamp";

    private readonly HooksInstaller _hooks = new();
    private readonly Label _hooksStatus = Ui.Label("", bold: true);
    private readonly Label _status = Ui.Label("");
    private readonly CheckBox _autostart = new() { Text = "Iniciar o Claude Lamp com o Windows (na bandeja)", AutoSize = true };
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
        _autostart.CheckedChanged += (_, _) => { if (!_loading) SetAutostart(_autostart.Checked); };
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
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            _autostart.Checked = key?.GetValue(RunValue) is string;
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

    private void SetAutostart(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) key.SetValue(RunValue, $"\"{Program.ExePath}\" --tray");
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
            _status.ForeColor = Ui.Ok;
            _status.Text = on ? "Vai iniciar com o Windows." : "Não vai mais iniciar com o Windows.";
        }
        catch (Exception ex) { _status.ForeColor = Ui.Error; _status.Text = ex.Message; }
    }
}
