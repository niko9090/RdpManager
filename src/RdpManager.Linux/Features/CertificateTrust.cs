using RdpManager.Platform.Linux;

namespace RdpManager.Linux.Features;

/// <summary>
/// Avviso certificato del server (#45) e riconnessione dopo un'uscita di FreeRDP.
/// FreeRDP, senza terminale, non può chiedere se fidarsi di un certificato: con «/cert:tofu» accetta la prima volta e
/// fallisce se poi il certificato cambia. Qui si riconosce quel fallimento e si chiede all'utente; se accetta, la sessione
/// riparte con «/cert:ignore» (solo per quella sessione).
/// Collegamento con il blocco A: quando <c>FreeRdpCommandBuilder</c> avrà il parametro <c>CertificatePolicy</c>, in
/// <c>FreeRdpSession.BuildCommand</c> si passa la politica «ignora» al posto di <see cref="AcceptUntrusted"/>.
/// </summary>
public static class CertificateTrust
{
    /// <summary>Codici d'uscita di FreeRDP per errori di TLS/certificato (XF_EXIT_TLS_CONNECT_FAILED e simili).</summary>
    private static readonly HashSet<int> CertificateExitCodes = [143];

    /// <summary>Vero se l'uscita di FreeRDP è dovuta al certificato del server (non fidato o cambiato).</summary>
    public static bool IsCertificateProblem(int exitCode, string stderr)
    {
        if (string.IsNullOrEmpty(stderr))
            return CertificateExitCodes.Contains(exitCode);
        var text = stderr.ToLowerInvariant();
        if (text.Contains("host key") && text.Contains("changed"))
            return true;
        if (text.Contains("certificate") && (text.Contains("verif") || text.Contains("not trusted") || text.Contains("changed")
                                              || text.Contains("mismatch") || text.Contains("expired") || text.Contains("untrusted")))
            return true;
        if (text.Contains("tls_verify_certificate") || text.Contains("certificate_data"))
            return true;
        return CertificateExitCodes.Contains(exitCode) && text.Contains("tls");
    }

    /// <summary>Testo dell'avviso: cosa è successo, con l'ultima riga utile di FreeRDP.</summary>
    public static string Describe(string host, string stderr)
    {
        var detail = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.Contains("certificate", StringComparison.OrdinalIgnoreCase) || l.Contains("host key", StringComparison.OrdinalIgnoreCase))
            ?? "";
        var text = $"Il certificato del server «{host}» non è quello già conosciuto o non è affidabile. "
                   + "Potrebbe essere stato rinnovato dall'amministratore, ma potrebbe anche essere un tentativo di intercettare la connessione.";
        return detail.Length > 0 ? text + "\n\nDettaglio: " + detail : text;
    }

    /// <summary>Lo stesso comando con il certificato accettato senza controllo (solo per la sessione in corso).</summary>
    public static FreeRdpCommand AcceptUntrusted(FreeRdpCommand command)
    {
        var args = command.Arguments.Select(a => a switch
        {
            "/cert:tofu" => "/cert:ignore",
            "/cert-tofu" => "/cert-ignore",
            _ => a,
        }).ToList();
        return command with { Arguments = args };
    }

    /// <summary>Codici d'uscita di FreeRDP per cui ha senso ritentare: rete, DNS, TLS di trasporto, errori sconosciuti.</summary>
    private static readonly HashSet<int> Retryable = [12, 130, 131, 137, 138, 139, 140, 141, 142, 146, 149, 255];

    /// <summary>
    /// Vero se dopo l'uscita con <paramref name="exitCode"/> la sessione va ritentata da sola: mai per chiusure volute
    /// (0, 1, 2, 3, 11), credenziali, account o certificato. Il numero di tentativi lo decide <c>ReconnectPolicy</c>.
    /// </summary>
    public static bool IsRetryableExit(int exitCode, string stderr) =>
        Retryable.Contains(exitCode) && !IsCertificateProblem(exitCode, stderr);
}
