namespace RdpManager.Core;

/// <summary>
/// Se il controllo RDP può mostrare la propria richiesta di credenziali («Sicurezza di Windows»).
/// Con una password salvata non deve mai farlo, né alla prima connessione né alle successive (nuova Connect dopo
/// una caduta): il controllo nuovo parte con la richiesta permessa, quindi va vietata a ogni connessione.
/// </summary>
public readonly record struct CredentialPromptPolicy(bool PromptForCredentials, bool AllowPrompting, bool Strict)
{
    /// <param name="hasPassword">La connessione effettiva (profilo risolto) ha una password.</param>
    /// <param name="allowPromptWhenNoPassword">Senza password si lascia chiedere a Windows (predefinito dell'app).</param>
    public static CredentialPromptPolicy For(bool hasPassword, bool allowPromptWhenNoPassword) =>
        new(PromptForCredentials: !hasPassword && allowPromptWhenNoPassword,
            AllowPrompting: !hasPassword && allowPromptWhenNoPassword,
            // Divieto esplicito (prove automatiche): se il controllo non lo accetta, non ci si connette.
            Strict: !allowPromptWhenNoPassword);

    /// <summary>Password per il controllo ActiveX dalla connessione effettiva, da impostare a ogni connessione.</summary>
    public static string? PasswordFor(RdpConnection effective) =>
        string.IsNullOrEmpty(effective.Password) ? null : effective.Password;
}
