namespace RdpManager.Core;

/// <summary>Quando e dopo quanto ritentare la connessione dopo una disconnessione non voluta.</summary>
public static class ReconnectPolicy
{
    public const int DefaultMaxAttempts = 3;
    public const int MaxAllowedAttempts = 20;

    /// <summary>Vero se dopo <paramref name="attemptsDone"/> tentativi falliti se ne può fare un altro.</summary>
    public static bool CanRetry(RdpConnection c, int attemptsDone) =>
        c.AutoReconnect && attemptsDone < Math.Clamp(c.AutoReconnectMaxAttempts, 0, MaxAllowedAttempts);

    /// <summary>
    /// Vero se la disconnessione con codice <paramref name="reason"/> merita un nuovo tentativo automatico:
    /// non se l'ha chiesta l'utente né se ritentare non può servire (credenziali, account, certificato rifiutato).
    /// </summary>
    public static bool ShouldReconnect(RdpConnection c, int reason, int attemptsDone) =>
        CanRetry(c, attemptsDone) && !DisconnectReasons.IsUserInitiated(reason) && !NotRetryable.Contains(reason);

    private static readonly HashSet<int> NotRetryable = [263, 2055, 2567, 2823, 2825, 3079, 3335, 3591, 3847, 4615, 7943];

    /// <summary>Attesa prima del tentativo numero <paramref name="attempt"/> (da 1): 2, 4, 8, 16 secondi, poi sempre 30.</summary>
    public static TimeSpan DelayBefore(int attempt)
    {
        if (attempt <= 1)
            return TimeSpan.FromSeconds(2);
        return TimeSpan.FromSeconds(Math.Min(30, 2 << Math.Min(attempt - 1, 4)));
    }
}
