using ClaudeLamp.Core;

namespace ClaudeLamp.App;

/// <summary>Controle manual: ligar/desligar, cor, brilho e branco.</summary>
internal sealed class ControlTab : UserControl
{
    private readonly Panel _swatch = new() { Size = new Size(56, 56), BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(3, 3, 12, 3) };
    private readonly TrackBar _brightness = new() { Minimum = 1, Maximum = 100, TickFrequency = 10, Width = 320 };
    private readonly TrackBar _temp = new() { Minimum = 0, Maximum = 100, TickFrequency = 10, Width = 320 };
    private readonly Label _brightnessValue = Ui.Label("");
    private readonly Label _tempValue = Ui.Label("");
    private readonly Label _status = Ui.Label("");
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 300 };
    private ManualState _state = new();
    private bool _loading;

    public ControlTab()
    {
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };

        layout.Controls.Add(Ui.Label("Lâmpada", bold: true));
        layout.Controls.Add(Ui.Row(
            Ui.Button("Ligar", (_, _) => Change(s => s.On = true), 90),
            Ui.Button("Desligar", (_, _) => Change(s => s.On = false), 90),
            Ui.Button("Ler estado da lâmpada", async (_, _) => await ReadFromLampAsync())));

        layout.Controls.Add(Ui.Label("Cor", bold: true));
        var presets = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        foreach (var (name, hex) in TrayContext.Presets)
        {
            var b = new Button { Width = 34, Height = 34, BackColor = Ui.ParseOr(hex, Color.White), FlatStyle = FlatStyle.Flat, Margin = new Padding(2) };
            new ToolTip().SetToolTip(b, name);
            b.Click += (_, _) => Change(s => { s.On = true; s.Mode = "colour"; s.Color = hex; });
            presets.Controls.Add(b);
        }
        layout.Controls.Add(Ui.Row(_swatch, Ui.Button("Escolher cor...", (_, _) => PickColor()), presets));

        layout.Controls.Add(Ui.Label("Brilho", bold: true));
        _brightness.ValueChanged += (_, _) => { _brightnessValue.Text = $"{_brightness.Value}%"; if (!_loading) Change(s => { s.On = true; s.Brightness = _brightness.Value; }); };
        layout.Controls.Add(Ui.Row(_brightness, _brightnessValue));

        layout.Controls.Add(Ui.Label("Branco", bold: true));
        _temp.ValueChanged += (_, _) => { _tempValue.Text = TempText(_temp.Value); if (!_loading) Change(s => { s.On = true; s.Mode = "white"; s.WhiteTemp = _temp.Value; }); };
        layout.Controls.Add(Ui.Row(
            Ui.Button("Usar branco", (_, _) => Change(s => { s.On = true; s.Mode = "white"; s.WhiteTemp = _temp.Value; })),
            _temp, _tempValue));

        layout.Controls.Add(new Label { Height = 12 });
        layout.Controls.Add(_status);
        layout.Controls.Add(Ui.Label("Dica: clique no ícone da bandeja para ligar/desligar; botão direito para atalhos."));
        Controls.Add(layout);

        _debounce.Tick += async (_, _) => { _debounce.Stop(); await ApplyAsync(); };
        LampService.ManualStateChanged += s => { if (IsHandleCreated) BeginInvoke(() => { _state = s; Show(s); }); };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _state = Storage.LoadState().Manual;
        Show(_state);
    }

    private static string TempText(int t) => t switch { < 34 => $"{t} (quente)", < 67 => $"{t} (neutro)", _ => $"{t} (frio)" };

    private void PickColor()
    {
        using var dlg = new ColorDialog { FullOpen = true, Color = Ui.ParseOr(_state.Color, Color.White) };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var hex = Ui.ToRgb(dlg.Color).ToHex();
        Change(s => { s.On = true; s.Mode = "colour"; s.Color = hex; });
    }

    private void Change(Action<ManualState> change)
    {
        change(_state);
        Show(_state);
        _debounce.Stop();
        _debounce.Start();
    }

    private async Task ApplyAsync()
    {
        var snapshot = new ManualState
        {
            On = _state.On, Mode = _state.Mode, Color = _state.Color, Brightness = _state.Brightness, WhiteTemp = _state.WhiteTemp,
        };
        await Ui.Busy(_status, "Enviando para a lâmpada...", [], async () =>
        {
            await LampService.ApplyManualAsync(snapshot);
            return $"Aplicado às {DateTime.Now:HH:mm:ss}";
        });
    }

    private async Task ReadFromLampAsync()
    {
        await Ui.Busy(_status, "Lendo a lâmpada...", [], async () =>
        {
            var s = await LampService.RunAsync(lamp => lamp.ReadStateAsync());
            _state = s;
            Show(s);
            return $"{(s.On ? "Ligada" : "Desligada")}, modo {(s.Mode == "colour" ? "cor " + s.Color : "branco")}, brilho {s.Brightness}%";
        });
    }

    private void Show(ManualState s)
    {
        _loading = true;
        try
        {
            _swatch.BackColor = Ui.ColorOf(s);
            _brightness.Value = Math.Clamp(s.Brightness, 1, 100);
            _brightnessValue.Text = $"{_brightness.Value}%";
            _temp.Value = Math.Clamp(s.WhiteTemp, 0, 100);
            _tempValue.Text = TempText(_temp.Value);
        }
        finally { _loading = false; }
    }
}
