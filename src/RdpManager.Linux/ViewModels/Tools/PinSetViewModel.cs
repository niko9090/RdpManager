using RdpManager.Core;

namespace RdpManager.Linux.ViewModels.Tools;

/// <summary>Impostazione del PIN (#28): due volte, solo cifre. Il PIN non esce dal modello: si tiene solo l'impronta.</summary>
public sealed class PinSetViewModel : ViewModelBase
{
    private string _pin = "", _confirm = "";
    private string? _error;

    public string WindowTitle => "PIN di blocco";

    public string Note => $"Da {PinLock.MinLength} a {PinLock.MaxLength} cifre. Si salva solo un'impronta (PBKDF2): se lo dimentichi le connessioni " +
                          "restano, ma le password salvate vanno reinserite.";

    public int MaxLength => PinLock.MaxLength;

    public string Pin { get => _pin; set => Set(ref _pin, value); }

    public string ConfirmPin { get => _confirm; set => Set(ref _confirm, value); }

    public string? Error { get => _error; private set => Set(ref _error, value); }

    /// <summary>L'impronta del PIN scelto, dopo <see cref="TryCommit"/>.</summary>
    public PinHash? Result { get; private set; }

    public bool TryCommit()
    {
        Error = PinLock.Validate(Pin) ?? (Pin != ConfirmPin ? "I due PIN non coincidono." : null);
        if (Error is not null)
            return false;
        Result = PinLock.Create(Pin);
        return true;
    }
}
