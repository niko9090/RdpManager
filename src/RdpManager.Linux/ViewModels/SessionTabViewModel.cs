using RdpManager.Linux.Sessions;

namespace RdpManager.Linux.ViewModels;

/// <summary>Una scheda dell'area sessioni.</summary>
public sealed class SessionTabViewModel : ViewModelBase
{
    private bool _detached;

    public SessionTabViewModel(ISession session, Action<SessionTabViewModel> close)
    {
        Session = session;
        CloseCommand = new RelayCommand(() => close(this));
        session.TitleChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    public ISession Session { get; }

    public string Title => Session.Title;

    public object Content => Session.Content;

    public RelayCommand CloseCommand { get; }

    /// <summary>La sessione sta in una finestra staccata (la scheda resta, con la sessione altrove).</summary>
    public bool IsDetached
    {
        get => _detached;
        set => Set(ref _detached, value);
    }

    /// <summary>Sessione collegata (per saltare le schede disconnesse nel passaggio con la tastiera). Le sessioni senza stato contano come collegate.</summary>
    public bool IsConnected => Session is not FreeRdpSession f || f.IsConnected;
}
