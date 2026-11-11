using System.Text;

namespace RdpmSync;

/// <summary>Comandi da riga di comando (es. <c>docker exec -it rdpm-sync dotnet rdpm-sync.dll crea-account mario</c>).</summary>
public static class Cli
{
    public static int Run(string[] args, SyncOptions options, TextReader? input = null, TextWriter? output = null)
    {
        input ??= Console.In;
        output ??= Console.Out;
        switch (args[0])
        {
            case "crea-account" when args.Length == 2:
            {
                if (!Store.ValidUsername(args[1]))
                {
                    output.WriteLine("Nome utente non valido: da 3 a 64 caratteri tra lettere, cifre e . _ - @");
                    return 2;
                }
                var password = ReadPassword(input, output);
                if (password is null || password.Length < 8)
                {
                    output.WriteLine("La password deve avere almeno 8 caratteri (meglio una frase lunga).");
                    return 2;
                }
                var store = new Store(options.DatabasePath, TimeProvider.System);
                // Stessa derivazione del cliente: il server custodisce solo l'hash del segreto di accesso.
                var secret = ClientKeys.AuthSecret(args[1], password);
                if (!store.CreateAccount(args[1], secret, options.ServerHashIterations))
                {
                    output.WriteLine("Esiste già un account con questo nome.");
                    return 1;
                }
                output.WriteLine($"Account «{args[1]}» creato.");
                return 0;
            }
            case "lista-account" when args.Length == 1:
                foreach (var a in new Store(options.DatabasePath, TimeProvider.System).ListAccounts())
                    output.WriteLine($"{a.Username}\tcreato {a.Created:yyyy-MM-dd}\t{(a.HasDocument ? "con documento" : "senza documento")}");
                return 0;
            case "elimina-account" when args.Length == 2:
                if (!new Store(options.DatabasePath, TimeProvider.System).DeleteAccount(args[1]))
                {
                    output.WriteLine("Account inesistente.");
                    return 1;
                }
                output.WriteLine($"Account «{args[1]}» eliminato (con documento e token).");
                return 0;
            case "export-client-config":
                return ExportClientConfig(args, options, output);
            case "--health" when args.Length == 1:
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                    return http.GetAsync($"http://127.0.0.1:{options.Port}/health").GetAwaiter().GetResult().IsSuccessStatusCode ? 0 : 1;
                }
                catch (Exception)
                {
                    return 1;
                }
            default:
                output.WriteLine("Comandi: crea-account <utente> | lista-account | elimina-account <utente> | export-client-config --url <indirizzo> | --health");
                return 2;
        }
    }

    /// <summary>
    /// <c>export-client-config --url https://vpn.example.com:8443 [--url-interno http://10.66.0.1:8787] [--utente U --password P]
    /// [--senza-chiave-al-server]</c>: stampa il .rdpmconfig con i valori del server (redirigi l'uscita su un file).
    /// </summary>
    private static int ExportClientConfig(string[] args, SyncOptions options, TextWriter output)
    {
        string? url = null, inside = null, user = null, password = null;
        bool requestKey = true;
        for (int i = 1; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--url": url = Next(); break;
                case "--url-interno": inside = Next(); break;
                case "--utente": user = Next(); break;
                case "--password": password = Next(); break;
                case "--senza-chiave-al-server": requestKey = false; break;
                default:
                    output.WriteLine($"Opzione sconosciuta: {args[i]}");
                    return 2;
            }
        }
        var (json, errors) = ClientConfigExport.Build(options, url ?? "", inside, user, password, requestKey);
        if (json is null)
        {
            foreach (var e in errors)
                output.WriteLine(e);
            return 2;
        }
        output.Write(json);
        return 0;
    }

    private static string? ReadPassword(TextReader input, TextWriter output)
    {
        output.Write("Password dell'account (non verrà mostrata se il terminale lo permette): ");
        if (!Console.IsInputRedirected && ReferenceEquals(input, Console.In))
        {
            var sb = new StringBuilder();
            while (Console.ReadKey(intercept: true) is var k && k.Key != ConsoleKey.Enter)
            {
                if (k.Key == ConsoleKey.Backspace && sb.Length > 0)
                    sb.Length--;
                else if (!char.IsControl(k.KeyChar))
                    sb.Append(k.KeyChar);
            }
            output.WriteLine();
            return sb.ToString();
        }
        return input.ReadLine();
    }
}
