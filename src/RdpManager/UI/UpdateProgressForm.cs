using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>
/// Finestrella del programma di aggiornamento: «Aggiornamento di RdpManager in corso…» mentre la copia di riserva
/// aspetta la chiusura dell'app, installa in silenzio, verifica e riapre. Il lavoro vero è in <see cref="UpdateRunner"/>.
/// </summary>
internal sealed class UpdateProgressForm : Form
{
    private readonly Label _text = new() { Dock = DockStyle.Top, Height = 48, TextAlign = ContentAlignment.MiddleLeft };

    private UpdateProgressForm(string text)
    {
        Text = "Aggiornamento di RdpManager";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        Font = SystemFonts.MessageBoxFont ?? Font;
        ClientSize = new Size(440, 96);
        Padding = new Padding(16, 8, 16, 12);
        _text.Text = text;
        var bar = new ProgressBar { Dock = DockStyle.Bottom, Height = 18, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30 };
        Controls.Add(_text);
        Controls.Add(bar);
    }

    /// <summary>Esegue il programma di aggiornamento con la finestrella; non chiede nulla all'utente.</summary>
    public static void RunUpdate(string[] args)
    {
        var job = UpdateJob.TryParse(args);
        var fallbackLog = Path.Combine(AppLog.DefaultDirectory, "aggiornamento.log");
        if (job is null)
        {
            SystemUpdateEnvironment.AppendLog(fallbackLog, "Programma di aggiornamento avviato con argomenti non validi: non faccio nulla");
            return;
        }
        void Log(string line) => SystemUpdateEnvironment.AppendLog(job.LogPath, line);
        using var form = new UpdateProgressForm($"Aggiornamento di RdpManager alla {job.ExpectedVersion.ToString(3)} in corso…\nLe sessioni si riapriranno da sole.");
        form.Shown += async (_, _) =>
        {
            try
            {
                var result = await Task.Run(() => UpdateRunner.Run(job, new SystemUpdateEnvironment(), Log));
                Log("Esito: " + result.Outcome);
            }
            catch (Exception ex)
            {
                // Imprevisto: si prova almeno a riaprire l'app installata (o la copia di riserva) con il messaggio.
                Log("Errore imprevisto del programma di aggiornamento: " + ex);
                TryReopenAfterCrash(job, ex.Message);
            }
            form.Close();
        };
        Application.Run(form);
    }

    private static void TryReopenAfterCrash(UpdateJob job, string reason)
    {
        var message = $"L'aggiornamento a RdpManager {job.ExpectedVersion.ToString(3)} si è interrotto: {reason}\n\nDettagli nel log: {job.LogPath}";
        var args = new List<string>();
        if (job.DataFile is not null)
            args.AddRange(["--dati", job.DataFile]);
        args.AddRange([UpdateJob.FailedArgument, message]);
        var env = new SystemUpdateEnvironment();
        foreach (var dir in new[] { job.InstallDir, job.BackupDir })
        {
            var exe = Path.Combine(dir, UpdateJob.ExeName);
            if (env.FileVersion(exe) is null)
                continue;
            try
            {
                env.Launch(exe, args);
                return;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                SystemUpdateEnvironment.AppendLog(job.LogPath, "Riapertura non riuscita: " + ex.Message);
            }
        }
    }
}
