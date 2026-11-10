using System.Globalization;
using RdpManager.Core;
using RdpManager.Linux.ViewModels.Tools;

namespace RdpManager.Linux.ViewModels.Settings;

/// <summary>Pagina «PIN» delle Impostazioni (blocco A): PIN di blocco e blocco dopo inattività (#28).</summary>
public sealed class PinPageViewModel : SettingsPageViewModel
{
    public const int MaxMinutes = 24 * 60;

    private readonly AppSettings _settings;
    private readonly IDialogService? _dialogs;
    private PinHash? _pin;
    private string _lockAfter;
    private string? _error;

    public PinPageViewModel(AppSettings settings, IDialogService? dialogs = null)
    {
        _settings = settings;
        _dialogs = dialogs;
        _pin = settings.LockPin;
        _lockAfter = Math.Clamp(settings.LockAfterMinutes, 0, MaxMinutes).ToString(CultureInfo.InvariantCulture);
        SetPinCommand = new RelayCommand(() => _ = SetPinAsync());
        RemovePinCommand = new RelayCommand(RemovePin, () => _pin is not null);
    }

    public override string Title => "PIN";

    public string Note => "Si salva solo un'impronta del PIN (PBKDF2). Da bloccata RdpManager nasconde le sessioni, tiene le password cifrate e non apre " +
                          "sessioni nuove. Se dimentichi il PIN le connessioni restano, le password vanno reinserite.";

    public RelayCommand SetPinCommand { get; }

    public RelayCommand RemovePinCommand { get; }

    public bool HasPin => _pin is { IsValid: true };

    public string PinState => HasPin ? "PIN impostato" : "Nessun PIN: il blocco è spento";

    /// <summary>Minuti di inattività prima del blocco (0 = solo a mano).</summary>
    public string LockAfterMinutes { get => _lockAfter; set => Set(ref _lockAfter, value); }

    public string? Error { get => _error; private set => Set(ref _error, value); }

    /// <summary>Imposta o cambia il PIN (chiede il PIN due volte). Il PIN vale quando si preme Salva.</summary>
    public async Task SetPinAsync()
    {
        if (_dialogs is null)
            return;
        var vm = new PinSetViewModel();
        if (await _dialogs.ShowAsync(vm) && vm.Result is { } hash)
        {
            _pin = hash;
            Refresh();
        }
    }

    public void RemovePin()
    {
        _pin = null;
        Refresh();
    }

    /// <summary>Controlla i minuti; falso con <see cref="Error"/> impostato.</summary>
    public bool Validate()
    {
        if (int.TryParse(LockAfterMinutes.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int m) && m <= MaxMinutes)
        {
            Error = null;
            return true;
        }
        Error = $"I minuti di inattività devono essere un numero da 0 a {MaxMinutes}.";
        return false;
    }

    public override void Apply()
    {
        _settings.LockPin = _pin;
        if (Validate())
            _settings.LockAfterMinutes = int.Parse(LockAfterMinutes.Trim(), CultureInfo.InvariantCulture);
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(HasPin));
        OnPropertyChanged(nameof(PinState));
        RemovePinCommand.RaiseCanExecuteChanged();
    }
}
