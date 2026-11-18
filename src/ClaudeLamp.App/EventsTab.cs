using ClaudeLamp.Core;

namespace ClaudeLamp.App;

/// <summary>Configura a reação da lâmpada a cada evento do Claude Code.</summary>
internal sealed class EventsTab : UserControl
{
    public static string DisplayName(string evt) => evt switch
    {
        ClaudeEvents.Question => "Pergunta para você",
        ClaudeEvents.Plan => "Plano aguardando aprovação",
        ClaudeEvents.Permission => "Pedido de permissão",
        ClaudeEvents.Done => "Terminou",
        ClaudeEvents.Working => "Trabalhando",
        ClaudeEvents.Idle => "Ocioso (esperando você)",
        _ => evt,
    };

    private static readonly string[] Order =
        [ClaudeEvents.Question, ClaudeEvents.Plan, ClaudeEvents.Permission, ClaudeEvents.Done, ClaudeEvents.Working, ClaudeEvents.Idle];

    private sealed class RowControls
    {
        public required CheckBox Enabled, PcRgb;
        public required Button Color;
        public required NumericUpDown Brightness, Blink, Interval, Restore;
        public required Button Test;
    }

    private readonly CheckBox _signalsEnabled = new() { Text = "Sinais do Claude Code ativados", AutoSize = true, Margin = new Padding(3, 3, 3, 10) };
    private readonly Dictionary<string, RowControls> _rows = [];
    private readonly Label _status = Ui.Label("");

    public EventsTab()
    {
        var root = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        root.Controls.Add(_signalsEnabled);

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 9, Margin = new Padding(0) };
        string[] headers = ["Evento", "Ativo", "PC RGB", "Cor", "Brilho %", "Piscadas", "Intervalo ms", "Restaurar após s", ""];
        foreach (var h in headers) grid.Controls.Add(Ui.Label(h, bold: true));

        foreach (var evt in Order)
        {
            var row = new RowControls
            {
                Enabled = new CheckBox { AutoSize = true, Margin = new Padding(12, 8, 3, 3) },
                PcRgb = new CheckBox { AutoSize = true, Margin = new Padding(18, 8, 3, 3) },
                Color = new Button { Width = 70, Height = 26, FlatStyle = FlatStyle.Flat },
                Brightness = Num(1, 100),
                Blink = Num(0, 10),
                Interval = Num(100, 2000, 50),
                Restore = Num(0, 3600),
                Test = new Button { Text = "Testar", Width = 70, Height = 26 },
            };
            row.Color.Click += (_, _) => PickColor(row.Color);
            row.Test.Click += (_, _) => Test(evt);
            _rows[evt] = row;

            grid.Controls.Add(Ui.Label(DisplayName(evt)));
            grid.Controls.AddRange([row.Enabled, row.PcRgb, row.Color, row.Brightness, row.Blink, row.Interval, row.Restore, row.Test]);
        }
        root.Controls.Add(grid);
        root.Controls.Add(Ui.Label("\"PC RGB\" = também acende RAM, placa-mãe, teclado e mouse (aba Dispositivos RGB).\n\"Restaurar após\" = segundos até voltar à cor escolhida em Controle (0 = fica na cor do evento)."));
        root.Controls.Add(Ui.Row(
            Ui.Button("Salvar", (_, _) => Save(), 90),
            Ui.Button("Restaurar padrões", (_, _) => { Show(LampConfig.DefaultEvents(), _signalsEnabled.Checked); _status.Text = "Padrões carregados (clique Salvar)."; })));
        root.Controls.Add(_status);
        Controls.Add(root);
    }

    private static NumericUpDown Num(int min, int max, int step = 1) =>
        new() { Minimum = min, Maximum = max, Increment = step, Width = 80, Margin = new Padding(3, 4, 3, 3) };

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var cfg = Storage.LoadConfig();
        Show(cfg.Events, cfg.SignalsEnabled);
    }

    private void Show(Dictionary<string, SignalConfig> events, bool signalsEnabled)
    {
        _signalsEnabled.Checked = signalsEnabled;
        foreach (var (evt, row) in _rows)
        {
            var s = events.TryGetValue(evt, out var v) ? v : new SignalConfig();
            row.Enabled.Checked = s.Enabled;
            row.PcRgb.Checked = s.UsesPcRgb(evt);
            row.Color.BackColor = Ui.ParseOr(s.Color, Color.White);
            row.Color.Tag = Ui.ToRgb(row.Color.BackColor).ToHex();
            row.Brightness.Value = Math.Clamp(s.Brightness, 1, 100);
            row.Blink.Value = Math.Clamp(s.Blink, 0, 10);
            row.Interval.Value = Math.Clamp(s.BlinkIntervalMs, 100, 2000);
            row.Restore.Value = Math.Clamp(s.RestoreAfterSeconds ?? 0, 0, 3600);
        }
    }

    private void PickColor(Button b)
    {
        using var dlg = new ColorDialog { FullOpen = true, Color = b.BackColor };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        b.BackColor = dlg.Color;
        b.Tag = Ui.ToRgb(dlg.Color).ToHex();
    }

    /// <summary>Lê a tela para dentro de uma config (sem salvar).</summary>
    private LampConfig Collect()
    {
        var cfg = Storage.LoadConfig();
        cfg.SignalsEnabled = _signalsEnabled.Checked;
        foreach (var (evt, row) in _rows)
        {
            cfg.Events[evt] = new SignalConfig
            {
                Enabled = row.Enabled.Checked,
                PcRgb = row.PcRgb.Checked,
                Color = (string)row.Color.Tag!,
                Brightness = (int)row.Brightness.Value,
                Blink = (int)row.Blink.Value,
                BlinkIntervalMs = (int)row.Interval.Value,
                RestoreAfterSeconds = row.Restore.Value > 0 ? (int)row.Restore.Value : null,
            };
        }
        return cfg;
    }

    private void Save()
    {
        Storage.SaveConfig(Collect());
        _status.ForeColor = Ui.Ok;
        _status.Text = $"Salvo às {DateTime.Now:HH:mm:ss}.";
    }

    private void Test(string evt)
    {
        if (SignalHost.Current is null) { _status.ForeColor = Ui.Error; _status.Text = "O app ainda está iniciando."; return; }
        LampService.TestSignal(Collect(), evt);
        _status.ForeColor = Ui.Ok;
        _status.Text = $"Testando \"{DisplayName(evt)}\" (volta ao normal em até 5 s).";
    }
}
