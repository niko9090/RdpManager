using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>
/// «Strumenti → Plancia VPN»: i PC della VPN (nome, IP, collegato o no, ultimo contatto), gli stessi dati della pagina
/// web /vpn del server. Si aggiorna da sola ogni 30 secondi finché è aperta; da ogni PC si apre il desktop remoto.
/// </summary>
internal sealed class VpnDashboardForm : TouchForm
{
    private readonly Func<bool> _signedIn;
    private readonly Func<CancellationToken, Task<IReadOnlyList<VpnDashboardDevice>>> _load;
    private readonly Action<string, string> _open;
    private readonly Func<DateTimeOffset> _now;

    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
    };
    private readonly Label _message = new() { Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(760, 0), Padding = new Padding(0, 4, 0, 8) };
    private readonly Button _refresh = DialogStyle.Button("Aggiorna");
    private readonly Button _openRdp = DialogStyle.Button("Apri desktop remoto");
    private readonly Button _close = DialogStyle.Button("Chiudi", DialogResult.Cancel);
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = VpnDashboardModel.RefreshSeconds * 1000 };
    private readonly CancellationTokenSource _cts = new();
    private bool _loading;

    public VpnDashboardView Current { get; private set; } = VpnDashboardModel.NotSignedIn();

    public VpnDashboardForm(Func<bool> signedIn, Func<CancellationToken, Task<IReadOnlyList<VpnDashboardDevice>>> load,
        Action<string, string> open, Func<DateTimeOffset>? now = null)
    {
        _signedIn = signedIn;
        _load = load;
        _open = open;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        DialogStyle.Setup(this, "Plancia VPN", resizable: true);
        ShowInTaskbar = true;
        Size = new Size(820, 480);
        MinimumSize = new Size(560, 300);

        foreach (var (text, width) in new[] { ("Nome", 200), ("IP nella VPN", 130), ("Stato", 110), ("Ultimo contatto", 160) })
            _list.Columns.Add(text, width);
        _openRdp.Enabled = false;
        _list.SelectedIndexChanged += (_, _) => _openRdp.Enabled = SelectedRow is not null;
        _list.DoubleClick += (_, _) => OpenSelected();
        _openRdp.Click += (_, _) => OpenSelected();
        _refresh.Click += async (_, _) => await RefreshAsync();
        _close.Click += (_, _) => Close();

        var buttons = DialogStyle.Buttons(_refresh, _openRdp, _close);
        buttons.Dock = DockStyle.Bottom;
        Controls.Add(_list);
        Controls.Add(_message);
        Controls.Add(buttons);
        CancelButton = _close;

        _timer.Tick += async (_, _) => await RefreshAsync();
        Shown += async (_, _) =>
        {
            await RefreshAsync();
            _timer.Start();
        };
        FormClosed += (_, _) =>
        {
            _timer.Stop();
            _timer.Dispose();
            _cts.Cancel();
        };
    }

    private VpnDashboardRow? SelectedRow => _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as VpnDashboardRow : null;

    private void OpenSelected()
    {
        if (SelectedRow is { } row && VpnDashboardView.CanOpen(row))
            _open(row.Name, row.Address);
    }

    public async Task RefreshAsync()
    {
        if (_loading || IsDisposed)
            return;
        _loading = true;
        _refresh.Enabled = false;
        try
        {
            VpnDashboardView view;
            if (!_signedIn())
            {
                view = VpnDashboardModel.NotSignedIn();
            }
            else
            {
                try
                {
                    view = VpnDashboardModel.FromDevices(await _load(_cts.Token), _now());
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is SyncServerException or SyncUnreachableException or InvalidOperationException
                                               or HttpRequestException or IOException)
                {
                    view = VpnDashboardModel.FromError(ex);
                }
            }
            if (!IsDisposed)
                Show(view);
        }
        finally
        {
            _loading = false;
            if (!IsDisposed)
                _refresh.Enabled = true;
        }
    }

    private void Show(VpnDashboardView view)
    {
        Current = view;
        var selected = SelectedRow?.Address;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var row in view.Rows)
        {
            var item = new ListViewItem([row.Name, row.Address, row.StateText, row.LastContactText]) { Tag = row };
            item.ForeColor = row.Connected ? Color.ForestGreen : SystemColors.GrayText;
            item.Selected = row.Address == selected;
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        _message.Text = view.Message;
        _message.ForeColor = view.Status is VpnDashboardStatus.Ok or VpnDashboardStatus.Empty ? SystemColors.ControlText : Color.Firebrick;
        _openRdp.Enabled = SelectedRow is not null;
    }
}
