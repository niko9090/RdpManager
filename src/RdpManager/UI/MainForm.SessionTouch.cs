using RdpManager.Core;

namespace RdpManager.UI;

// Dito dentro la sessione remota (dalla 1.9.1): modalità del tocco (remoto o mouse), azioni extra della barra a
// schermo intero e linguetta per richiamare la barra col dito (col dito non c'è il bordo del mouse da sfiorare).
public sealed partial class MainForm
{
    private Button? _fsHandle;

    /// <summary>Per le prove: simula uno schermo a tocco (null = si chiede a Windows).</summary>
    internal bool? TouchDeviceForTests { get; set; }

    private bool HasTouchScreen => TouchDeviceForTests ?? SessionTouchInput.HasTouchScreen;

    /// <summary>Linguetta per richiamare la barra a schermo intero; si vede solo su uno schermo a tocco, a schermo intero, con la barra nascosta.</summary>
    internal Button? FullScreenHandle => _fsHandle;

    /// <summary>Dà alla sessione la modalità del tocco scelta e si ricorda la scelta.</summary>
    private void WireSessionTouch(RdpSessionView view)
    {
        view.TouchMode = _doc.Settings.SessionTouch;
        view.GestureMap = _doc.Settings.GestureMap;
        view.TouchCommandRequested += (_, kind) => ExecuteTouchCommand(kind);
        view.TouchModeToggleRequested += (_, _) => SetSessionTouchMode(SessionTouchModes.Toggle(_doc.Settings.SessionTouch));
    }

    /// <summary>Gesti a più dita per la finestra: scheda precedente o successiva, barra comandi della sessione (a schermo intero).</summary>
    internal void ExecuteTouchCommand(TouchActionKind kind)
    {
        if (_locked)
            return;
        switch (kind)
        {
            case TouchActionKind.PreviousTab:
                CycleTab(-1);
                break;
            case TouchActionKind.NextTab:
                CycleTab(1);
                break;
            case TouchActionKind.ShowBar:
                ShowFullScreenBarFromHandle();
                break;
            case TouchActionKind.HideBar:
                ExecuteTouchSessionAction(SessionBarAction.HideBar);
                break;
        }
    }

    /// <summary>Cambia la modalità del tocco per tutte le sessioni e la salva.</summary>
    internal void SetSessionTouchMode(SessionTouchMode mode)
    {
        _doc.Settings.SessionTouch = mode;
        foreach (var v in AllViews)
            v.TouchMode = mode;
        SaveConnections();
    }

    /// <summary>Azioni sulla sessione nate col dito (la barra a schermo intero le richiama da <see cref="ExecuteSessionAction"/>).</summary>
    private void ExecuteTouchSessionAction(SessionBarAction action)
    {
        switch (action)
        {
            case SessionBarAction.SendEscape:
                CurrentView?.SendEscape();
                break;
            case SessionBarAction.SendWindows:
                CurrentView?.SendWindowsKey();
                break;
            case SessionBarAction.RightClick:
                CurrentView?.ArmRightClick();
                break;
            case SessionBarAction.TouchMode:
                SetSessionTouchMode(SessionTouchModes.Toggle(_doc.Settings.SessionTouch));
                break;
            case SessionBarAction.HideBar:
                if (FullScreenBarShown)
                    ToggleFullScreenBar();
                UpdateFullScreenHandle();
                break;
        }
    }

    /// <summary>Mostra o nasconde la linguetta e la mette sul bordo della barra, al centro.</summary>
    private void UpdateFullScreenHandle()
    {
        bool show = _fullScreen && !FullScreenBarShown && !_locked && HasTouchScreen;
        if (!show)
        {
            if (_fsHandle is not null)
                _fsHandle.Visible = false;
            return;
        }
        if (_fsHandle is null)
        {
            _fsHandle = new Button
            {
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(40, 90, 160), ForeColor = Color.White, TabStop = false,
                UseMnemonic = false, UseVisualStyleBackColor = false, Font = new Font(Font.FontFamily, 18f, FontStyle.Bold),
            };
            _fsHandle.FlatAppearance.BorderSize = 0;
            _fsHandle.Click += (_, _) => ShowFullScreenBarFromHandle();
            _tips.SetToolTip(_fsHandle, "Mostra la barra");
            _root.Controls.Add(_fsHandle);
        }
        int depth = TouchUi.Px(this, TouchMetrics.SessionTarget), length = TouchUi.Px(this, 160);
        var area = _root.ClientRectangle;
        var position = _doc.Settings.Bar.Position;
        _fsHandle.Text = position switch { BarPosition.Right => "‹", BarPosition.Bottom => "˄", _ => "›" };
        _fsHandle.Bounds = position switch
        {
            BarPosition.Right => new Rectangle(area.Right - depth, area.Top + (area.Height - length) / 2, depth, length),
            BarPosition.Bottom => new Rectangle(area.Left + (area.Width - length) / 2, area.Bottom - depth, length, depth),
            _ => new Rectangle(area.Left, area.Top + (area.Height - length) / 2, depth, length),
        };
        _fsHandle.Visible = true;
        _fsHandle.BringToFront();
    }

    private void ShowFullScreenBarFromHandle()
    {
        if (!_fullScreen || FullScreenBarShown)
            return;
        if (_doc.Settings.Bar.Mode == BarMode.Fixed)
        {
            _fsBarHiddenByUser = false;
            LayoutOverlays();
            RebuildFullScreenBar();
        }
        else
        {
            RevealFullScreenBar();
        }
        UpdateFullScreenHandle();
    }
}
