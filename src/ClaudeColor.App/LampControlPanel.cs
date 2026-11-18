using ClaudeColor.Core;

namespace ClaudeColor.App;

/// <summary>Controle manual da lâmpada: ligar/desligar, cor, brilho e branco.</summary>
internal sealed class LampControlPanel : FlowLayoutPanel
{
    private readonly Panel _swatch = new() { Size = new Size(34, 34), Margin = new Padding(0, 4, 10, 4) };
    private readonly TrackBar _brightness = new() { Minimum = 1, Maximum = 100, TickFrequency = 10, Width = 260, TickStyle = TickStyle.None };
    private readonly TrackBar _temp = new() { Minimum = 0, Maximum = 100, TickFrequency = 10, Width = 260, TickStyle = TickStyle.None };
    private readonly Label _brightnessValue = Ui.Label("");
    private readonly Label _tempValue = Ui.Label("");
    private readonly Label _status = Ui.Status();
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 300 };
    private ManualState _state = new();
    private bool _loading;

    public LampControlPanel()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        Margin = Padding.Empty;

        Controls.Add(Ui.Hint("O estado escolhido aqui é o \"normal\" da lâmpada: é para ele que ela volta depois de cada sinal."));

        var presets = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        foreach (var (name, hex) in TrayContext.Presets)
            presets.Controls.Add(PresetButton(name, hex));

        var form = Ui.Form(90);
        Ui.AddField(form, "Energia", Ui.Row(
            Ui.Button("Ligar", (_, _) => Change(s => s.On = true)),
            Ui.Button("Desligar", (_, _) => Change(s => s.On = false)),
            Ui.Button("Ler da lâmpada", async (_, _) => await ReadFromLampAsync())));
        Ui.AddField(form, "Cor", Ui.Row(_swatch, presets, Ui.Button("Outra...", (_, _) => PickColor())));
        Ui.AddField(form, "Brilho", Ui.Row(_brightness, _brightnessValue));
        Ui.AddField(form, "Branco", Ui.Row(_temp, _tempValue, Ui.Button("Usar branco", (_, _) => Change(s => { s.On = true; s.Mode = "white"; s.WhiteTemp = _temp.Value; }))));
        Controls.Add(form);
        Controls.Add(_status);

        _brightness.ValueChanged += (_, _) => { _brightnessValue.Text = $"{_brightness.Value}%"; if (!_loading) Change(s => { s.On = true; s.Brightness = _brightness.Value; }); };
        _temp.ValueChanged += (_, _) => { _tempValue.Text = TempText(_temp.Value); if (!_loading) Change(s => { s.On = true; s.Mode = "white"; s.WhiteTemp = _temp.Value; }); };
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await ApplyAsync(); };
        LampService.ManualStateChanged += s => { if (IsHandleCreated) BeginInvoke(() => { _state = s; Show(s); }); };
    }

    private Control PresetButton(string name, string hex)
    {
        var b = new Button
        {
            Width = 26, Height = 26, BackColor = Ui.ParseOr(hex, Color.White), FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 4, 4, 4), Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderColor = Ui.Border;
        new ToolTip().SetToolTip(b, name);
        b.Click += (_, _) => Change(s => { s.On = true; s.Mode = "colour"; s.Color = hex; });
        return b;
    }

    public void Reload()
    {
        _state = Storage.LoadState().Manual;
        Show(_state);
    }

    private static string TempText(int t) => t switch { < 34 => $"{t} (quente)", < 67 => $"{t} (neutro)", _ => $"{t} (frio)" };

    private void PickColor()
    {
        using var dlg = new ColorDialog { FullOpen = true, Color = Ui.ParseOr(_state.Color, Color.White) };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
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
