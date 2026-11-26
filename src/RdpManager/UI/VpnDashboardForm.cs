using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>
/// Menu «Plancia»: i PC della VPN con il riepilogo «N di M in linea», la ricerca, il nome scelto dall'utente (e il nome
/// vero del PC), IP, stato (cerchio verde o grigio) e ultimo contatto: gli stessi dati della pagina web /vpn del server.
/// Si aggiorna da sola ogni 30 secondi finché è aperta. Da ogni PC si apre il desktop remoto, si inviano file e si
/// sceglie il nome da mostrare: è il nome della connessione nella cartella marshmelloVPN, sincronizzato con gli altri PC.
/// </summary>
internal sealed class VpnDashboardForm : TouchForm
{
    private readonly Func<bool> _signedIn;
    private readonly Func<CancellationToken, Task<IReadOnlyList<VpnDashboardDevice>>> _load;
    private readonly Action<string, string> _open;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<string, string, CancellationToken, Task>? _sendFile;
    private readonly Func<IReadOnlyDictionary<string, string>> _names;
    private readonly Func<VpnDashboardRow, string, bool>? _rename;

    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
        BorderStyle = BorderStyle.None,
    };
    private readonly Label _summary = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12f, FontStyle.Bold), Margin = new Padding(0, 6, 0, 0) };
    private readonly TextBox _search = new() { Width = 260, PlaceholderText = "Cerca per nome, IP o proprietario" };
    private readonly Label _message = new() { Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(820, 0), Padding = new Padding(0, 4, 0, 8) };
    private readonly Button _refresh = DialogStyle.Button("Aggiorna");
    private readonly Button _renameButton = DialogStyle.Button("Rinomina…");
    private readonly Button _openRdp = DialogStyle.Button("Apri desktop remoto");
    private readonly Button _sendFileButton = DialogStyle.Button("Invia file…");
    private readonly Button _close = DialogStyle.Button("Chiudi", DialogResult.Cancel);
    private readonly ToolStripMenuItem _miOpen = new("Apri desktop remoto");
    private readonly ToolStripMenuItem _miRename = new("Rinomina…");
    private readonly ToolStripMenuItem _miReset = new("Torna al nome originale");
    private readonly ToolStripMenuItem _miSend = new("Invia file…");
    private readonly ToolStripMenuItem _miCopy = new("Copia l'indirizzo IP");
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = VpnDashboardModel.RefreshSeconds * 1000 };
    private readonly CancellationTokenSource _cts = new();
    private bool _loading;
    private Font? _bold;

    public VpnDashboardView Current { get; private set; } = VpnDashboardModel.NotSignedIn();

    public VpnDashboardForm(Func<bool> signedIn, Func<CancellationToken, Task<IReadOnlyList<VpnDashboardDevice>>> load,
        Action<string, string> open, Func<DateTimeOffset>? now = null, Func<string, string, CancellationToken, Task>? sendFile = null,
        Func<IReadOnlyDictionary<string, string>>? names = null, Func<VpnDashboardRow, string, bool>? rename = null)
    {
        _signedIn = signedIn;
        _load = load;
        _open = open;
        _sendFile = sendFile;
        _names = names ?? (() => new Dictionary<string, string>());
        _rename = rename;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        DialogStyle.Setup(this, "Plancia marshmelloVPN", resizable: true);
        ShowInTaskbar = true;
        Size = new Size(900, 540);
        MinimumSize = new Size(620, 320);

        foreach (var (text, width) in new[] { ("Nome", 220), ("Nome del PC", 170), ("IP nella VPN", 120), ("Stato", 100), ("Ultimo contatto", 150) })
            _list.Columns.Add(text, width);
        _list.SmallImageList = StatusImages();

        _sendFileButton.Visible = sendFile is not null;
        _miSend.Visible = sendFile is not null;
        _list.ContextMenuStrip = new ContextMenuStrip();
        _list.ContextMenuStrip.Items.AddRange([_miOpen, _miRename, _miReset, _miSend, new ToolStripSeparator(), _miCopy]);
        _list.ContextMenuStrip.Opening += (_, e) => e.Cancel = SelectedRow is null;
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.DoubleClick += (_, _) => OpenSelected();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F2) Rename();
            else if (e.KeyCode == Keys.Enter) OpenSelected();
        };
        _openRdp.Click += (_, _) => OpenSelected();
        _miOpen.Click += (_, _) => OpenSelected();
        _renameButton.Click += (_, _) => Rename();
        _miRename.Click += (_, _) => Rename();
        _miReset.Click += (_, _) => { if (SelectedRow is { } row) ApplyName(row, ""); };
        _miCopy.Click += (_, _) => { if (SelectedRow is { } row) Clipboard.SetText(row.Address); };
        _sendFileButton.Click += async (_, _) => await SendFileAsync();
        _miSend.Click += async (_, _) => await SendFileAsync();
        _refresh.Click += async (_, _) => await RefreshAsync();
        _close.Click += (_, _) => Close();
        _search.TextChanged += (_, _) => Fill();

        // In cima: riepilogo a sinistra, ricerca a destra.
        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 0, 0, 6) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(_summary, 0, 0);
        top.Controls.Add(_search, 1, 0);

        var buttons = DialogStyle.Buttons(_refresh, _renameButton, _sendFileButton, _openRdp, _close);
        buttons.Dock = DockStyle.Bottom;
        Controls.Add(_list);
        Controls.Add(_message);
        Controls.Add(top);
        Controls.Add(buttons);
        CancelButton = _close;
        UpdateButtons();

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

    /// <summary>Cerchi dello stato: 0 = in linea (verde), 1 = non in linea (grigio).</summary>
    private static ImageList StatusImages()
    {
        var images = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
        foreach (var color in new[] { UiColors.Connected, UiColors.Unreachable })
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var brush = new SolidBrush(color);
                g.FillEllipse(brush, 3, 3, 10, 10);
            }
            images.Images.Add(bmp);
        }
        return images;
    }

    private VpnDashboardRow? SelectedRow => _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as VpnDashboardRow : null;

    private void UpdateButtons()
    {
        var row = SelectedRow;
        _openRdp.Enabled = _miOpen.Enabled = row is not null && VpnDashboardView.CanOpen(row);
        _sendFileButton.Enabled = _miSend.Enabled = row is not null && !_sending;
        _renameButton.Enabled = _miRename.Enabled = _rename is not null && row is { Key.Length: > 0 };
        _miReset.Enabled = _rename is not null && row is { IsRenamed: true };
        _miCopy.Enabled = row is { Address.Length: > 0 };
    }

    private void OpenSelected()
    {
        if (SelectedRow is { } row && VpnDashboardView.CanOpen(row))
            _open(row.Name, row.Address);
    }

    /// <summary>«Rinomina…» (anche F2): il nome da mostrare per il PC; vuoto = il nome originale.</summary>
    private void Rename()
    {
        if (_rename is null || SelectedRow is not { Key.Length: > 0 } row)
            return;
        var name = TextPromptDialog.Ask(this, "Rinomina PC", $"Nome da mostrare per «{row.Device}» (vuoto = nome originale):", row.Name);
        if (name is not null)
            ApplyName(row, name);
    }

    private void ApplyName(VpnDashboardRow row, string name)
    {
        if (_rename is null || !_rename(row, name))
            return;
        Current = VpnDashboardModel.WithNames(Current, _names());
        Fill(row.Address);
    }

    private bool _sending;

    /// <summary>«Invia file…»: sceglie il file, lo manda al PC selezionato e dice com'è andata sotto l'elenco.</summary>
    private async Task SendFileAsync()
    {
        if (_sendFile is null || _sending || SelectedRow is not { } row)
            return;
        using var dlg = new OpenFileDialog { Title = $"Invia a {row.Name}", CheckFileExists = true };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        _sending = true;
        UpdateButtons();
        _message.ForeColor = SystemColors.ControlText;
        _message.Text = $"Invio di «{Path.GetFileName(dlg.FileName)}» a {row.Name}…";
        try
        {
            await _sendFile(row.Device, dlg.FileName, _cts.Token);
            if (!IsDisposed)
                _message.Text = VpnFileModel.SentText(row.Name, Path.GetFileName(dlg.FileName));
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is SyncServerException or SyncUnreachableException or InvalidOperationException
                                       or HttpRequestException or IOException or UnauthorizedAccessException)
        {
            if (!IsDisposed)
            {
                _message.ForeColor = Color.Firebrick;
                _message.Text = VpnFileModel.SendError(ex);
            }
        }
        finally
        {
            _sending = false;
            if (!IsDisposed)
                UpdateButtons();
        }
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
                    view = VpnDashboardModel.FromDevices(await _load(_cts.Token), _now(), _names());
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
        Fill();
        _message.Text = view.Message;
        _message.Visible = view.Message.Length > 0;
        _message.ForeColor = view.Status is VpnDashboardStatus.Ok or VpnDashboardStatus.Empty ? SystemColors.ControlText : Color.Firebrick;
    }

    /// <summary>Riempie l'elenco con le righe che passano la ricerca, mantenendo la selezione.</summary>
    private void Fill(string? select = null)
    {
        var selected = select ?? SelectedRow?.Address;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var row in Current.Rows.Where(r => VpnDashboardModel.Matches(r, _search.Text)))
        {
            var item = new ListViewItem([row.Name, row.IsRenamed ? row.DeviceName : "", row.Address, row.StateText, row.LastContactText])
            {
                Tag = row, ImageIndex = row.Connected ? 0 : 1, ToolTipText = row.Owner is { } o ? "Proprietario: " + o : "",
            };
            item.Font = _bold ??= new Font(_list.Font, FontStyle.Bold);
            item.UseItemStyleForSubItems = false;
            for (int i = 1; i < item.SubItems.Count; i++)
                item.SubItems[i].ForeColor = row.Connected ? _list.ForeColor : SystemColors.GrayText;
            if (!row.Connected)
                item.ForeColor = SystemColors.GrayText;
            item.Selected = row.Address == selected;
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        _list.ShowItemToolTips = true;
        _summary.Text = VpnDashboardModel.Summary(Current);
        UpdateButtons();
    }
}
