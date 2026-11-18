namespace ClaudeLamp.App;

/// <summary>Janela de configurações. Fechar só esconde; o app continua na bandeja.</summary>
internal sealed class SettingsForm : Form
{
    public const int ControlTabIndex = 0, EventsTabIndex = 1, ConnectionTabIndex = 2, SystemTabIndex = 3;

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Padding = new Point(12, 4) };

    public SettingsForm()
    {
        Text = "Claude Lamp — Configurações";
        Font = SystemFonts.MessageBoxFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 560);
        Size = new Size(900, 620);

        AddTab("Controle", new ControlTab());
        AddTab("Eventos do Claude", new EventsTab());
        AddTab("Conexão", new ConnectionTab());
        AddTab("Sistema", new SystemTab());
        Controls.Add(_tabs);
    }

    private void AddTab(string title, Control content)
    {
        var page = new TabPage(title) { Padding = new Padding(10), UseVisualStyleBackColor = true, AutoScroll = true };
        content.Dock = DockStyle.Fill;
        page.Controls.Add(content);
        _tabs.TabPages.Add(page);
    }

    public void SelectTab(int index) => _tabs.SelectedIndex = index;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}
