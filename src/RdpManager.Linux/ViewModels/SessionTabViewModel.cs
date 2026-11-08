using RdpManager.Linux.Sessions;

namespace RdpManager.Linux.ViewModels;

/// <summary>Una scheda dell'area sessioni.</summary>
public sealed class SessionTabViewModel : ViewModelBase
{
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
}
