using System.Text;
using System.Text.Json;
using RdpManager.Core;
using RdpManager.Linux.Services;
using RdpManager.Linux.ViewModels.Tools;

namespace RdpManager.Linux.ViewModels;

// Blocco A: importa .rdp / .rdpm / CSV (#20, #21, #22), esporta .rdpm / .rdp (#24, #25).
public sealed partial class MainWindowViewModel
{
    // --- Importa file .rdp (#20) ---

    /// <summary>Importa uno o più file .rdp nella cartella selezionata.</summary>
    public async Task ImportRdpAsync()
    {
        if (IsLocked)
            return;
        var files = await _dialogs.PickFilesToOpenAsync("Importa file .rdp", "Connessioni Desktop remoto (*.rdp)", ["*.rdp"], multiple: true);
        if (files.Count == 0)
            return;
        await ImportRdpFilesAsync(files);
    }

    internal async Task ImportRdpFilesAsync(IReadOnlyList<string> files)
    {
        if (IsLocked)
            return;
        var group = SelectedNode?.FolderPath ?? "";
        var errors = new List<string>();
        var added = new List<RdpConnection>();
        foreach (var file in files)
        {
            try
            {
                var path = file;
                var c = await Task.Run(() => RdpFile.Load(path));
                if (string.IsNullOrWhiteSpace(c.Host))
                    throw new InvalidDataException("indirizzo («full address») mancante");
                c.Group = group;
                added.Add(c);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        // Si aggiunge tutto alla fine: durante le letture (asincrone) l'elenco non cambia sotto i piedi.
        Document.Connections.AddRange(added);
        Save();
        RebuildTree();
        SelectedNode = added.Count == 0 ? SelectedNode : Flatten(Roots).FirstOrDefault(n => n.Connection == added[^1]);
        await _dialogs.ShowMessageAsync("Importa .rdp",
            $"Importate {added.Count} connessioni" + (group.Length > 0 ? $" nella cartella «{group}»." : ".") +
            (errors.Count > 0 ? "\n\nNon importati:\n" + string.Join('\n', errors) : ""));
        CheckAfterImport(added);
    }

    // --- Importa file .rdpm (#21) ---

    /// <summary>Importa un backup .rdpm: chiede la password del file se serve, poi unisci o sostituisci.</summary>
    public async Task ImportRdpmAsync()
    {
        if (IsLocked)
            return;
        var files = await _dialogs.PickFilesToOpenAsync("Importa connessioni", "Connessioni RdpManager (*.rdpm)", ["*.rdpm"], multiple: false);
        if (files.Count == 0)
            return;
        var path = files[0];
        var name = Path.GetFileName(path);
        ConnectionsDocument imported;
        try
        {
            var json = await File.ReadAllTextAsync(path);
            string? password = null;
            bool retry = false;
            while (true)
            {
                if (RdpmFile.IsEncrypted(json))
                {
                    var label = (retry ? "Password sbagliata. Riprova.\n" : "") + $"«{name}» contiene password cifrate.";
                    password = await _dialogs.PromptSecretAsync("Password del file", label);
                    if (password is null)
                        return;
                }
                try
                {
                    imported = await Task.Run(() => RdpmFile.Import(json, password));
                    break;
                }
                catch (RdpmPasswordException)
                {
                    retry = true;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
        {
            await _dialogs.ShowMessageAsync("Importa", $"«{name}» non si può importare: {ex.Message}");
            return;
        }

        var options = new RdpmImportViewModel(imported.Connections.Count, Document.Connections.Count,
            imported.Connections.Any(c => c.HasPassword) || imported.CredentialProfiles.Any(p => p.HasPassword),
            imported.SettingsIncluded, text => _dialogs.ConfirmDestructiveAsync("Importa connessioni", text));
        if (!await _dialogs.ShowAsync(options))
            return;
        await ApplyRdpmImportAsync(imported, options.Mode, options.ApplySettingsChosen);
    }

    /// <summary>Porta nell'elenco le connessioni (e i profili, le password…) di un .rdpm già letto, salva e mostra il riepilogo.</summary>
    internal async Task<ImportResult> ApplyRdpmImportAsync(ConnectionsDocument imported, ImportMode mode, bool applySettings)
    {
        var result = RdpmFile.ApplyImport(Document, imported, mode, applySettings);
        Save();
        RebuildTree();
        if (result.SettingsApplied)
        {
            LogSetup.ApplySettings(Document.Settings);
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
        AppLog.Default?.Info($"Importazione .rdpm: {result.Added} aggiunte, {result.Skipped} doppioni, {result.Removed} sostituite, " +
                             $"{result.PasswordsImported} password, {result.ProfilesAdded} profili, impostazioni {(result.SettingsApplied ? "applicate" : "no")}");
        await _dialogs.ShowMessageAsync("Importazione completata", result.Summary());
        CheckAfterImport(Document.Connections.ToList());
        return result;
    }

    // --- Importa da CSV (#22) ---

    public async Task ImportCsvAsync()
    {
        if (IsLocked)
            return;
        var files = await _dialogs.PickFilesToOpenAsync("Importa connessioni da CSV", "File CSV (*.csv, *.txt)", ["*.csv", "*.txt"], multiple: false);
        if (files.Count == 0)
            return;
        CsvTable table;
        try
        {
            table = CsvImport.Parse(ReadText(files[0]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowMessageAsync("Importa CSV", "Impossibile leggere il file: " + ex.Message);
            return;
        }
        if (table.Header.Count == 0 || table.Rows.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Importa CSV", "Il file non ha righe da importare (serve un'intestazione e almeno una riga).");
            return;
        }
        var vm = new CsvImportViewModel(Path.GetFileName(files[0]), table, Document, SelectedNode?.FolderPath ?? "");
        if (!await _dialogs.ShowAsync(vm))
            return;
        var added = CsvImport.ImportInto(Document, vm.Rows);
        Save();
        RebuildTree();
        SelectedNode = added.Count == 0 ? SelectedNode : Flatten(Roots).FirstOrDefault(n => n.Connection == added[^1]);
        StatusMessage = $"Importate {added.Count} connessioni dal CSV.";
        AppLog.Default?.Info($"Importazione CSV: {added.Count} connessioni");
        CheckAfterImport(added);
    }

    /// <summary>UTF-8 (con o senza BOM); se non è UTF-8 valido, come lo salva Excel in italiano (Latin-1).</summary>
    internal static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    // --- Esporta tutto in .rdpm (#24) ---

    public async Task ExportRdpmAsync()
    {
        if (IsLocked)
            return;
        if (Document.Connections.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Esporta", "Non ci sono connessioni da esportare.");
            return;
        }
        var options = new RdpmExportViewModel(Document, text => _dialogs.ConfirmAsync("Esporta connessioni", text));
        if (!await _dialogs.ShowAsync(options))
            return;
        var path = await _dialogs.PickFileToSaveAsync("Esporta connessioni", $"connessioni-{DateTime.Now:yyyy-MM-dd}{RdpmFile.Extension}",
            RdpmFile.Extension.TrimStart('.'), "Connessioni RdpManager (*.rdpm)");
        if (path is null)
            return;
        var password = options.FilePassword;
        var includeSettings = options.IncludeSettings;
        try
        {
            StatusMessage = "Esportazione in corso…";
            await Task.Run(() => File.WriteAllText(path,
                RdpmFile.Export(Document, password, KdfSettings.ExportIterations, includeSettings), new System.Text.UTF8Encoding(false)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusMessage = "";
            await _dialogs.ShowMessageAsync("Esporta", "Esportazione non riuscita: " + ex.Message);
            return;
        }
        int withPassword = password is null ? 0 : RdpmFile.CountExportablePasswords(Document);
        var profiles = Document.CredentialProfiles.Count > 0 ? $", {Document.CredentialProfiles.Count} profili" : "";
        StatusMessage = $"Esportate {Document.Connections.Count} connessioni{profiles}{(includeSettings ? " e le impostazioni" : "")} in {Path.GetFileName(path)}" +
                        (password is null ? " (senza password)" : $" ({withPassword} password cifrate)") +
                        (password is not null && RdpmFile.CountUnreadablePasswords(Document) is > 0 and var lost ? $"; {lost} non leggibili su questo computer" : "");
        AppLog.Default?.Info($"Esportazione .rdpm: {Document.Connections.Count} connessioni, impostazioni {(includeSettings ? "sì" : "no")}, password {(password is null ? "escluse" : "cifrate")}");
    }

    // --- Esporta una connessione come file .rdp (#25) ---

    /// <summary>
    /// Salva la connessione selezionata come file .rdp per altri client (con il gateway e l'utente del profilo, se ci sono).
    /// Senza password: una password cifrata per questo computer non servirebbe altrove.
    /// </summary>
    public async Task ExportRdpFileAsync()
    {
        if (IsLocked)
            return;
        if (SelectedNode?.Connection is not { } c)
            return;
        var safe = string.Concat(c.DisplayName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var path = await _dialogs.PickFileToSaveAsync("Esporta come file .rdp", safe + ".rdp", "rdp", "Connessioni Desktop remoto (*.rdp)");
        if (path is null)
            return;
        try
        {
            RdpFile.Save(path, Document.WithResolvedCredentials(c), includePassword: false);
            StatusMessage = $"«{c.DisplayName}» esportata in {Path.GetFileName(path)} (senza password)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowMessageAsync("Esporta", "Esportazione non riuscita: " + ex.Message);
        }
    }

    /// <summary>Dopo un'importazione si controlla la raggiungibilità dei PC nuovi (solo con i controlli in sottofondo accesi).</summary>
    private void CheckAfterImport(IReadOnlyList<RdpConnection> added)
    {
        if (_backgroundChecks && added.Count > 0 && !IsLocked)
            _ = CheckReachabilityAsync(added, announce: false);
    }
}
