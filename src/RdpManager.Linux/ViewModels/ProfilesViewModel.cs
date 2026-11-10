using System.Collections.ObjectModel;
using RdpManager.Core;

namespace RdpManager.Linux.ViewModels;

/// <summary>Gestione dei profili di credenziali condivisi: elenco a sinistra, modulo per quello selezionato.</summary>
public sealed class ProfilesViewModel : ViewModelBase
{
    private readonly ConnectionsDocument _doc;
    private readonly Func<bool> _save;
    private readonly IDialogService _dialogs;
    private CredentialProfile? _selected;
    private bool _isNew;
    private string _name = "", _username = "", _domain = "", _password = "";
    private string? _error;

    /// <param name="save">Salva il documento; falso se non è riuscito.</param>
    public ProfilesViewModel(ConnectionsDocument doc, Func<bool> save, IDialogService dialogs)
    {
        _doc = doc;
        _save = save;
        _dialogs = dialogs;
        foreach (var p in doc.CredentialProfiles.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            Items.Add(p);
        NewCommand = new RelayCommand(New);
        SaveCommand = new RelayCommand(() => Commit());
        DeleteCommand = new RelayCommand(() => _ = DeleteAsync(), () => _selected is not null && !_isNew);
        Selected = Items.FirstOrDefault();
        if (Selected is null)
            New();
    }

    public ObservableCollection<CredentialProfile> Items { get; } = [];

    public RelayCommand NewCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand DeleteCommand { get; }

    public CredentialProfile? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value) || value is null)
                return;
            _isNew = false;
            Load(value);
            DeleteCommand.RaiseCanExecuteChanged();
        }
    }

    public string Name { get => _name; set => Set(ref _name, value); }
    public string Username { get => _username; set => Set(ref _username, value); }
    public string Domain { get => _domain; set => Set(ref _domain, value); }

    /// <summary>Vuota = non cambia la password già salvata.</summary>
    public string Password { get => _password; set => Set(ref _password, value); }

    public string? Error { get => _error; private set => Set(ref _error, value); }

    public bool IsNew => _isNew;

    /// <summary>Prepara il modulo per un profilo nuovo.</summary>
    public void New()
    {
        _selected = null;
        OnPropertyChanged(nameof(Selected));
        _isNew = true;
        Name = Username = Domain = Password = "";
        Error = null;
        DeleteCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Applica il modulo al profilo selezionato (o ne crea uno nuovo) e salva.</summary>
    public bool Commit()
    {
        if (Name.Trim().Length == 0 && Username.Trim().Length == 0)
        {
            Error = "Indica almeno un nome o un utente.";
            return false;
        }
        var profile = _isNew || _selected is null ? new CredentialProfile() : _selected;
        profile.Name = Name.Trim();
        profile.Username = Username.Trim();
        profile.Domain = Domain.Trim();
        if (Password.Length > 0)
        {
            profile.Password = Password;
            profile.ProtectedPassword = null;
        }
        if (_isNew || _selected is null)
            _doc.CredentialProfiles.Add(profile);

        Error = null;
        Refresh(profile);
        return _save();
    }

    public async Task DeleteAsync()
    {
        if (_selected is not { } profile || _isNew)
            return;
        int uses = _doc.ConnectionsUsingProfile(profile.Id).Count;
        var warning = uses == 0
            ? $"Eliminare il profilo «{profile.DisplayName}»?"
            : $"Il profilo «{profile.DisplayName}» è usato da {uses} connessioni: manterranno utente e dominio ma non la password. Eliminarlo?";
        if (!await _dialogs.ConfirmDestructiveAsync("Elimina profilo", warning))
            return;
        _doc.DeleteProfile(profile.Id);
        Items.Remove(profile);
        Selected = Items.FirstOrDefault();
        if (Selected is null)
            New();
        _save();
    }

    private void Refresh(CredentialProfile profile)
    {
        // Ricostruisce l'elenco perché il nome mostrato (DisplayName) può essere cambiato.
        Items.Clear();
        foreach (var p in _doc.CredentialProfiles.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            Items.Add(p);
        _selected = null;
        Selected = profile;
    }

    private void Load(CredentialProfile profile)
    {
        Name = profile.Name;
        Username = profile.Username;
        Domain = profile.Domain;
        Password = "";
        Error = null;
    }
}
