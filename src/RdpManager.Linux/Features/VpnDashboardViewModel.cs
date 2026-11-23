using System.Collections.ObjectModel;
using RdpManager.Core;
using RdpManager.Linux.ViewModels;

namespace RdpManager.Linux.Features;

/// <summary>Una riga della plancia VPN: il testo viene da <see cref="VpnDashboardModel"/> (lo stesso di Windows).</summary>
public sealed record VpnDashboardItem(VpnDashboardRow Row)
{
    public string Name => Row.Name;
    public string Address => Row.Address;
    public string StateText => Row.StateText;
    public string LastContactText => Row.LastContactText;
    public bool Connected => Row.Connected;

    /// <summary>Cerchio dello stato: verde se collegato, grigio altrimenti (colori di <see cref="UiColors"/>).</summary>
    public string StatusColor => Row.Connected ? UiColors.Connected : UiColors.Unreachable;
}

/// <summary>
/// «Strumenti → Plancia VPN»: i PC della VPN (collegati per primi), gli stessi dati della pagina web /vpn del server.
/// La finestra chiama <see cref="RefreshAsync"/> ogni <see cref="VpnDashboardModel.RefreshSeconds"/> secondi.
/// </summary>
public sealed class VpnDashboardViewModel : ViewModelBase
{
    private readonly Func<bool> _signedIn;
    private readonly Func<CancellationToken, Task<IReadOnlyList<VpnDashboardDevice>>> _load;
    private readonly Func<DateTimeOffset> _now;
    private readonly CancellationTokenSource _cts = new();
    private VpnDashboardItem? _selected;
    private VpnDashboardView _view = VpnDashboardModel.NotSignedIn();
    private bool _loading;

    public VpnDashboardViewModel(Func<bool> signedIn, Func<CancellationToken, Task<IReadOnlyList<VpnDashboardDevice>>> load,
        Func<DateTimeOffset>? now = null)
    {
        _signedIn = signedIn;
        _load = load;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !Loading);
        OpenCommand = new RelayCommand(OpenSelected, () => CanOpen);
    }

    public ObservableCollection<VpnDashboardItem> Rows { get; } = [];

    public RelayCommand RefreshCommand { get; }
    public RelayCommand OpenCommand { get; }

    /// <summary>Il PC da cui l'utente ha chiesto il desktop remoto; la finestra si chiude e chi l'ha aperta lo collega.</summary>
    public VpnDashboardItem? Chosen { get; private set; }

    /// <summary>Scatta quando <see cref="Chosen"/> è stato scelto: la finestra si chiude.</summary>
    public event EventHandler? CloseRequested;

    public VpnDashboardView Current => _view;

    public VpnDashboardItem? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
                OpenCommand.RaiseCanExecuteChanged();
        }
    }

    public bool CanOpen => Selected is { } s && VpnDashboardView.CanOpen(s.Row);

    public bool Loading
    {
        get => _loading;
        private set
        {
            if (Set(ref _loading, value))
                RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Messaggio d'errore (account mancante, server che non risponde…); null se tutto bene.</summary>
    public string? ErrorText => _view.Status is VpnDashboardStatus.Ok or VpnDashboardStatus.Empty ? null : _view.Message;

    /// <summary>Messaggio non d'errore (elenco vuoto).</summary>
    public string InfoText => _view.Status == VpnDashboardStatus.Empty ? _view.Message : "";

    public bool HasInfo => InfoText.Length > 0;

    public void OpenSelected()
    {
        if (Selected is not { } item || !VpnDashboardView.CanOpen(item.Row))
            return;
        Chosen = item;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ferma una lettura in corso (alla chiusura della finestra).</summary>
    public void Cancel() => _cts.Cancel();

    public async Task RefreshAsync()
    {
        if (Loading || _cts.IsCancellationRequested)
            return;
        Loading = true;
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
            Show(view);
        }
        finally
        {
            Loading = false;
        }
    }

    private void Show(VpnDashboardView view)
    {
        _view = view;
        var selected = Selected?.Address;
        Rows.Clear();
        foreach (var row in view.Rows)
            Rows.Add(new VpnDashboardItem(row));
        Selected = selected is null ? null : Rows.FirstOrDefault(r => r.Address == selected);
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(InfoText));
        OnPropertyChanged(nameof(HasInfo));
        OpenCommand.RaiseCanExecuteChanged();
    }
}
