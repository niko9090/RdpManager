using System.Globalization;
using RdpManager.Core;

namespace RdpManager.Linux.ViewModels.Tools;

/// <summary>Impostazioni comuni di una cartella: profilo di credenziali e risoluzione, ereditati dalle connessioni contenute (#5).</summary>
public sealed class FolderSettingsViewModel : ViewModelBase
{
    private static readonly ChoiceItem<Guid?> NoProfile = new(null, "(nessuno: ogni connessione ha le sue)");

    private readonly ConnectionsDocument _doc;
    private bool _useResolution, _fixed;
    private string _width, _height;
    private string? _error;
    private ChoiceItem<Guid?> _profile;

    public FolderSettingsViewModel(ConnectionsDocument doc, string path)
    {
        _doc = doc;
        Path = RdpConnection.NormalizeGroup(path);
        var current = doc.FindFolderSettings(Path);
        Profiles = [NoProfile, .. doc.CredentialProfiles
            .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new ChoiceItem<Guid?>(p.Id, p.DisplayName))];
        _profile = Profiles.FirstOrDefault(p => p.Value is not null && p.Value == current?.CredentialProfileId) ?? NoProfile;
        _useResolution = current?.Resolution is not null;
        _fixed = current?.Resolution == ResolutionMode.Fixed;
        _width = Math.Clamp(current?.DesktopWidth ?? 1920, 640, 8192).ToString(CultureInfo.InvariantCulture);
        _height = Math.Clamp(current?.DesktopHeight ?? 1080, 480, 8192).ToString(CultureInfo.InvariantCulture);
    }

    public string Path { get; }

    public string WindowTitle => $"Impostazioni della cartella «{Path}»";

    public IReadOnlyList<ChoiceItem<Guid?>> Profiles { get; }

    public ChoiceItem<Guid?> Profile { get => _profile; set => Set(ref _profile, value); }

    /// <summary>«Imposta una risoluzione»: se spenta le connessioni non ereditano nulla.</summary>
    public bool UseResolution
    {
        get => _useResolution;
        set { if (Set(ref _useResolution, value)) { OnPropertyChanged(nameof(ResolutionEnabled)); OnPropertyChanged(nameof(SizeEnabled)); } }
    }

    /// <summary>Risoluzione fissa (altrimenti «adatta alla finestra»).</summary>
    public bool FixedResolution
    {
        get => _fixed;
        set { if (Set(ref _fixed, value)) { OnPropertyChanged(nameof(FitResolution)); OnPropertyChanged(nameof(SizeEnabled)); } }
    }

    public bool FitResolution { get => !_fixed; set => FixedResolution = !value; }

    public bool ResolutionEnabled => _useResolution;

    public bool SizeEnabled => _useResolution && _fixed;

    public string DesktopWidth { get => _width; set => Set(ref _width, value); }

    public string DesktopHeight { get => _height; set => Set(ref _height, value); }

    public string? Error { get => _error; private set => Set(ref _error, value); }

    /// <summary>Controlla i campi e scrive le impostazioni sul documento. Falso con <see cref="Error"/> impostato.</summary>
    public bool TryCommit()
    {
        int width = 1920, height = 1080;
        if (SizeEnabled
            && (!int.TryParse(DesktopWidth.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out width) || width is < 640 or > 8192
                || !int.TryParse(DesktopHeight.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out height) || height is < 480 or > 8192))
        {
            Error = "Larghezza (640-8192) e altezza (480-8192) devono essere numeri.";
            return false;
        }
        ResolutionMode? mode = !UseResolution ? null : FixedResolution ? ResolutionMode.Fixed : ResolutionMode.FitToWindow;
        _doc.SetFolderSettings(Path, Profile.Value, mode, width, height);
        Error = null;
        return true;
    }
}
