using ClaudeColor.Core;

namespace ClaudeColor.App;

/// <summary>Combinações de controladores: o Padrão (só OpenRGB) e os perfis montados pelo usuário.</summary>
internal sealed class ProfilesPage : Page
{
    private sealed record Item(DeviceProfile Profile, bool Active)
    {
        public override string ToString() =>
            $"{(Active ? "●  " : "     ")}{Profile.Name}{(Profiles.IsDefault(Profile.Name) ? " (somente leitura)" : "")}   —   {Profile.Summary()}";
    }

    private readonly Action<Type> _navigate;
    private readonly ListBox _list = new()
    {
        Width = 640, Height = 150, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false, Font = Ui.Body,
        Margin = new Padding(0, 4, 0, 4),
    };
    private readonly TextBox _name = new() { Width = 260 };
    private readonly Label _active = Ui.InlineStatus();
    private readonly Label _status = Ui.Status();
    private readonly Button _activate, _create, _rename, _delete, _edit;

    public override string NavTitle => "Perfis";

    public ProfilesPage(Action<Type> navigate) : base("Perfis",
        "Cada perfil é uma combinação de controladores. Troque de perfil para testar o que funciona melhor no seu PC.")
    {
        _navigate = navigate;

        var card = Add(new Card("Perfis",
            "O Padrão usa só o OpenRGB, com todos os dispositivos, e não pode ser alterado. Quando o OpenRGB sozinho não servir " +
            "(um software do fabricante disputando um dispositivo, por exemplo), crie um perfil, combine as integrações nativas " +
            "na página Dispositivos e volte ao Padrão quando quiser."));
        _activate = Ui.Button("Ativar", async (_, _) => await ActivateAsync(), primary: true);
        _edit = Ui.Button("Editar dispositivos", async (_, _) =>
        {
            if (Selected is { } s && !s.Active) await ActivateAsync();
            _navigate(typeof(DevicesPage));
        });
        _create = Ui.Button("Novo perfil (copia o selecionado)", (_, _) => Change(cfg =>
        {
            var from = Selected?.Profile.Name ?? cfg.ActiveProfile;
            var name = _name.Text.Trim().Length > 0 && cfg.Find(_name.Text) is null ? _name.Text : cfg.FreeName("Meu perfil");
            var p = cfg.CreateProfile(name, from);
            return (p.Name, $"Perfil \"{p.Name}\" criado a partir de \"{from}\". Ative-o e escolha os dispositivos.");
        }));
        _rename = Ui.Button("Renomear", (_, _) => Change(cfg =>
        {
            var from = Selected?.Profile.Name ?? "";
            cfg.RenameProfile(from, _name.Text);
            return (_name.Text.Trim(), $"Perfil renomeado para \"{_name.Text.Trim()}\".");
        }));
        _delete = Ui.Button("Excluir", async (_, _) => await DeleteAsync());

        var form = Ui.Form(60);
        Ui.AddField(form, "Nome", _name, _rename);
        card.Add(_active, _list, Ui.Row(_activate, _edit, _create), form, Ui.Row(_delete, _status));

        var tips = Add(new Card("Como montar um perfil"));
        tips.Add(
            tips.Wrap(Ui.Hint("1. Comece pelo Padrão e use \"Testar\" em Dispositivos. Se tudo acende e volta ao normal, não precisa de mais nada.")),
            tips.Wrap(Ui.Hint("2. Se um dispositivo não acende ou fica \"brigando\" com outro software, crie um perfil, desmarque-o no OpenRGB e " +
                              "ative a integração nativa do fabricante (iCUE, G HUB ou Mystic Light).")),
            tips.Wrap(Ui.Hint("3. A página Solução de problemas mostra o que cada controlador do perfil ativo precisa (SDKs, administrador, servidor).")),
            tips.Wrap(Ui.Hint("Ao trocar de perfil, os dispositivos do perfil anterior voltam ao modo que tinham antes do sinal.")));

        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.DoubleClick += async (_, _) => await ActivateAsync();
        ProfileService.Changed += () => { if (!IsDisposed && IsHandleCreated && Visible) BeginInvoke(OnShown); };
    }

    private Item? Selected => _list.SelectedItem as Item;

    public override void OnShown() => Reload(null);

    private void Reload(string? select)
    {
        var cfg = Storage.LoadConfig();
        select ??= Selected?.Profile.Name ?? cfg.ActiveProfile;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var p in cfg.Profiles)
            _list.Items.Add(new Item(p, string.Equals(p.Name, cfg.ActiveProfile, StringComparison.OrdinalIgnoreCase)));
        _list.EndUpdate();
        var index = cfg.Profiles.FindIndex(p => string.Equals(p.Name, select, StringComparison.OrdinalIgnoreCase));
        _list.SelectedIndex = index >= 0 ? index : 0;
        Ui.SetStatus(_active, true, $"Perfil ativo: {cfg.Active().Name}");
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var s = Selected;
        bool isDefault = s is null || Profiles.IsDefault(s.Profile.Name);
        _activate.Enabled = s is { Active: false };
        _rename.Enabled = _delete.Enabled = !isDefault;
        _name.Text = s is null || isDefault ? "" : s.Profile.Name;
    }

    private async Task ActivateAsync()
    {
        if (Selected is not { Active: false } s) return;
        await Ui.Busy(_status, "Trocando de perfil...", [_activate, _create, _rename, _delete, _edit], async () =>
        {
            await ProfileService.ActivateAsync(s.Profile.Name);
            Reload(s.Profile.Name);
            return $"Perfil \"{s.Profile.Name}\" ativo.";
        });
    }

    private async Task DeleteAsync()
    {
        if (Selected is not { } s || Profiles.IsDefault(s.Profile.Name)) return;
        if (MessageBox.Show(FindForm(), $"Excluir o perfil \"{s.Profile.Name}\"?", "Claude Color", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        await Ui.Busy(_status, "Excluindo...", [_activate, _create, _rename, _delete, _edit], async () =>
        {
            if (s.Active) await ProfileService.ActivateAsync(Profiles.DefaultName);
            var cfg = Storage.LoadConfig();
            cfg.DeleteProfile(s.Profile.Name);
            Storage.SaveConfig(cfg);
            ProfileService.RaiseChanged();
            Reload(cfg.ActiveProfile);
            return $"Perfil \"{s.Profile.Name}\" excluído.";
        });
    }

    /// <summary>Altera os perfis na config, grava e seleciona o perfil retornado.</summary>
    private void Change(Func<AppConfig, (string Select, string Message)> change)
    {
        try
        {
            var cfg = Storage.LoadConfig();
            var (select, message) = change(cfg);
            Storage.SaveConfig(cfg);
            ProfileService.RaiseChanged();
            Reload(select);
            _status.ForeColor = Ui.Ok;
            _status.Text = message;
        }
        catch (InvalidOperationException ex)
        {
            _status.ForeColor = Ui.Error;
            _status.Text = ex.Message;
        }
    }
}
