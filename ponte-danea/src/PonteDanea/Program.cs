using System.Globalization;
using System.Reflection;
using PonteDanea;

return await Avvio.EseguiAsync(args, Console.Out, Console.Error);

namespace PonteDanea
{
    public static class Avvio
    {
        private const string Uso = """
            Ponte Danea: prende le fatture dall'archivio Easyfatt e le manda a GLOS.

              PonteDanea [--config file.json] [--giorni N | --da AAAA-MM-GG] [--fino AAAA-MM-GG] [--prova] [--zip file.zip]

              --config   file di configurazione (default: ponte-danea.json accanto al programma)
              --giorni   le fatture degli ultimi N giorni (default: giorniIndietro della configurazione, 30)
              --da       le fatture da questa data
              --fino     fino a questa data (facoltativo)
              --prova    prepara lo zip e NON lo spedisce (scrive ponte-danea-prova.zip, o il file di --zip)
              --zip      dove salvare lo zip (in prova di default ponte-danea-prova.zip; se si spedisce, solo se indicato)
            """;

        public static async Task<int> EseguiAsync(string[] args, TextWriter o, TextWriter e)
        {
            try
            {
                var opz = Opzioni.Leggi(args);
                if (opz.Aiuto) { o.WriteLine(Uso); return 0; }

                var conf = Configurazione.Carica(opz.Config ?? Path.Combine(AppContext.BaseDirectory, "ponte-danea.json"));
                var mancanti = conf.Mancanti(opz.Prova);
                if (mancanti.Count > 0) throw new ErroreUtente("Nella configurazione mancano: " + string.Join(", ", mancanti) + ".");

                var oggi = DateTime.Today;
                var da = opz.Da ?? oggi.AddDays(-(opz.Giorni ?? conf.GiorniIndietro));
                var info = new InfoPacchetto(
                    Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0",
                    string.IsNullOrWhiteSpace(conf.NomePc) ? Environment.MachineName : conf.NomePc!,
                    Path.GetFileName(conf.Archivio));

                o.WriteLine($"Fatture dal {da:dd/MM/yyyy}" + (opz.Fino.HasValue ? $" al {opz.Fino:dd/MM/yyyy}" : "") + (opz.Prova ? " (prova: non si spedisce)" : "") + "…");
                Pacchetto pacchetto;
                using (var archivio = ArchivioFirebird.ApriCopia(conf))
                    pacchetto = CostruttorePacchetto.Costruisci(archivio, da, opz.Fino, info);

                o.WriteLine($"Nello zip: {pacchetto.Fatture} fatture, {pacchetto.Righe} righe, {pacchetto.Scadenze} scadenze, {pacchetto.Soggetti} clienti ({pacchetto.Zip.Length / 1024} KB).");

                var fileZip = opz.Zip ?? (opz.Prova ? "ponte-danea-prova.zip" : null);
                if (fileZip is not null)
                {
                    File.WriteAllBytes(fileZip, pacchetto.Zip);
                    o.WriteLine($"Zip salvato in {Path.GetFullPath(fileZip)}.");
                }

                if (opz.Prova)
                {
                    o.WriteLine("Prova finita: niente è stato spedito.");
                    return 0;
                }

                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                var esito = await new Spedizione(http).InviaAsync(conf.IndirizzoPacchetto(), conf.Token!, pacchetto.Zip, info);
                if (!esito.Ok)
                {
                    e.WriteLine($"GLOS non ha accettato il pacchetto ({esito.Stato}): {esito.Messaggio}");
                    return 2;
                }
                o.WriteLine($"GLOS: {esito.Esito} — {esito.Messaggio}");
                return 0;
            }
            catch (ErroreUtente x)
            {
                e.WriteLine(x.Message);
                return 1;
            }
            catch (HttpRequestException x)
            {
                e.WriteLine($"Non riesco a raggiungere GLOS: {x.Message}");
                return 2;
            }
            catch (TaskCanceledException)
            {
                e.WriteLine("GLOS non ha risposto in tempo.");
                return 2;
            }
        }
    }

    public sealed class Opzioni
    {
        public string? Config { get; private set; }
        public int? Giorni { get; private set; }
        public DateTime? Da { get; private set; }
        public DateTime? Fino { get; private set; }
        public bool Prova { get; private set; }
        public string? Zip { get; private set; }
        public bool Aiuto { get; private set; }

        public static Opzioni Leggi(string[] args)
        {
            var o = new Opzioni();
            for (var i = 0; i < args.Length; i++)
            {
                string Valore() => ++i < args.Length ? args[i] : throw new ErroreUtente($"Dopo {args[i - 1]} manca il valore.");
                switch (args[i])
                {
                    case "--prova": o.Prova = true; break;
                    case "--config": o.Config = Valore(); break;
                    case "--zip": o.Zip = Valore(); break;
                    case "--giorni":
                        o.Giorni = int.TryParse(Valore(), NumberStyles.None, CultureInfo.InvariantCulture, out var g) && g > 0
                            ? g : throw new ErroreUtente("--giorni vuole un numero intero maggiore di zero.");
                        break;
                    case "--da": o.Da = Data(args[i], Valore()); break;
                    case "--fino": o.Fino = Data(args[i], Valore()); break;
                    case "--aiuto" or "-h" or "--help": o.Aiuto = true; break;
                    default: throw new ErroreUtente($"Opzione sconosciuta: {args[i]}. Con --aiuto si vede l'elenco.");
                }
            }
            if (o.Giorni.HasValue && o.Da.HasValue) throw new ErroreUtente("--giorni e --da non vanno insieme.");
            if (o.Da.HasValue && o.Fino.HasValue && o.Fino < o.Da) throw new ErroreUtente("--fino è prima di --da.");
            return o;
        }

        private static DateTime Data(string opzione, string testo) =>
            DateTime.TryParseExact(testo, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d : throw new ErroreUtente($"{opzione} vuole una data AAAA-MM-GG.");
    }
}
