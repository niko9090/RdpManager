using System.Drawing;

namespace RdpManager.Core;

/// <summary>Timer a colpo singolo: l'interfaccia lo fa con un Timer di WinForms, le prove con uno finto.</summary>
public interface IResizeTimer
{
    /// <summary>Riparte da zero: scatta una sola volta dopo <paramref name="delayMs"/>.</summary>
    void Restart(int delayMs);

    void Stop();

    event Action? Elapsed;
}

/// <summary>Cosa può fare il coordinatore sulla sessione; l'implementazione vera è nella scheda di sessione.</summary>
public interface IResizeTarget
{
    bool IsConnected { get; }

    /// <summary>Scheda visibile (attiva) con una superficie di dimensioni vere.</summary>
    bool IsShown { get; }

    ResolutionMode Mode { get; }

    Size ClientSize { get; }

    /// <summary>Scala del desktop (100, 125, 150…) del monitor su cui sta la finestra.</summary>
    int Scale { get; }

    /// <summary>Invia la nuova risoluzione al server. false (o eccezione) = non riuscito, si riprova.</summary>
    bool TryUpdate(Size size, int scale);

    /// <summary>Risoluzione fissa: aggiorna solo lo zoom dell'immagine.</summary>
    void UpdateSmartSizing();

    /// <summary>Dopo troppi tentativi falliti: il server non supporta l'aggiornamento, si scala l'immagine.</summary>
    void GiveUp();
}

/// <summary>
/// Decide quando e con che dimensione aggiornare la risoluzione della sessione. Ogni evento (ridimensionamento,
/// cambio DPI, schermo, scheda mostrata, fine del login) richiede un aggiornamento; il lavoro parte dopo
/// <see cref="DelayMs"/> dall'ultimo evento. Se la sessione non è connessa o la scheda è nascosta la richiesta
/// resta in sospeso e riparte da sola alla prima occasione; se l'invio fallisce si riprova.
/// </summary>
public sealed class SessionResizeCoordinator
{
    public const int DelayMs = 400;
    public const int RetryDelayMs = 1000;
    public const int MaxFailures = 4;

    private readonly IResizeTarget _target;
    private readonly IResizeTimer _timer;
    private Size _lastSize;
    private int _lastScale;
    private int _failures;

    public SessionResizeCoordinator(IResizeTarget target, IResizeTimer timer)
    {
        _target = target;
        _timer = timer;
        _timer.Elapsed += OnElapsed;
    }

    public bool Pending { get; private set; }

    public bool GaveUp { get; private set; }

    /// <summary>Ultimi valori già noti al server (impostati alla connessione).</summary>
    public void SetBaseline(Size size, int scale)
    {
        _lastSize = size;
        _lastScale = scale;
    }

    /// <summary>Nuova connessione: si riparte da capo.</summary>
    public void Reset()
    {
        _timer.Stop();
        Pending = false;
        _failures = 0;
        GaveUp = false;
    }

    /// <summary>Qualcosa è cambiato (finestra, DPI, schermo, scheda mostrata, login finito): si aggiorna a breve.</summary>
    public void Request()
    {
        Pending = true;
        _timer.Restart(DelayMs);
    }

    private void OnElapsed()
    {
        _timer.Stop();
        if (!Pending)
            return;
        // Non connessa o scheda nascosta: resta in sospeso. Ci pensano il login finito e la scheda mostrata a richiamare Request.
        if (!_target.IsConnected || !_target.IsShown)
            return;
        if (_target.Mode == ResolutionMode.Fixed)
        {
            Pending = false;
            _target.UpdateSmartSizing();
            return;
        }
        if (_target.Mode != ResolutionMode.FitToWindow || GaveUp)
        {
            Pending = false;
            return;
        }
        var size = Clamp(_target.ClientSize);
        int scale = _target.Scale;
        if (size == _lastSize && scale == _lastScale)
        {
            Pending = false;
            return;
        }
        bool ok;
        try { ok = _target.TryUpdate(size, scale); }
        catch (Exception) { ok = false; }
        if (ok)
        {
            _lastSize = size;
            _lastScale = scale;
            _failures = 0;
            Pending = false;
            return;
        }
        if (++_failures >= MaxFailures)
        {
            GaveUp = true;
            Pending = false;
            _target.GiveUp();
            return;
        }
        _timer.Restart(RetryDelayMs);
    }

    public static Size Clamp(Size s) =>
        new(Math.Clamp(s.Width - s.Width % 2, 640, 8192), Math.Clamp(s.Height, 480, 8192));
}
