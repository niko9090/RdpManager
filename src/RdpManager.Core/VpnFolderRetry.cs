namespace RdpManager.Core;

/// <param name="Changed">L'elenco delle connessioni è cambiato.</param>
/// <param name="Failure">Perché l'aggiornamento non è riuscito (null = riuscito o niente da fare).</param>
public sealed record VpnFolderRefresh(bool Changed, string? Failure)
{
    public bool Failed => Failure is not null;
}

/// <summary>
/// Quando riprovare l'aggiornamento della cartella «marshmelloVPN» dopo un fallimento: 30 s, 1 min, 2 min, poi di nuovo
/// ai 5 minuti del giro normale. Un successo riparte da capo.
/// </summary>
public sealed class VpnFolderRetry
{
    public static readonly TimeSpan[] Steps = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)];
    public static readonly TimeSpan Normal = TimeSpan.FromMinutes(5);

    private int _failures;

    public int Failures => _failures;

    /// <summary>Un fallimento: restituisce fra quanto riprovare.</summary>
    public TimeSpan OnFailure() => _failures < Steps.Length ? Steps[_failures++] : Normal;

    /// <summary>Un successo (o una VPN appena accesa): si riparte da capo.</summary>
    public TimeSpan OnSuccess()
    {
        _failures = 0;
        return Normal;
    }
}
