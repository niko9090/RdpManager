using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using RdpManager.Linux.ViewModels.Tools;

namespace RdpManager.Linux.Dialogs.Tools;

/// <summary>Base delle finestre degli strumenti: titolo, dimensioni, centrata sul proprietario.</summary>
internal abstract class ToolWindow : Window
{
    protected ToolWindow(object viewModel, string title, double width, double height, bool resizable = true)
    {
        DataContext = viewModel;
        Title = title;
        Width = width;
        Height = height;
        MinWidth = Math.Min(width, 420);
        MinHeight = Math.Min(height, 300);
        CanResize = resizable;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }
}

/// <summary>Impostazioni della cartella (#5).</summary>
internal sealed class FolderSettingsWindow : ToolWindow
{
    public FolderSettingsWindow(FolderSettingsViewModel vm) : base(vm, vm.WindowTitle, 560, 420)
    {
        var profile = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = vm.Profiles }
            .Bind(SelectingItemsControl.SelectedItemProperty, nameof(vm.Profile), BindingMode.TwoWay);
        var use = new CheckBox { Content = "Imposta una risoluzione" }
            .Bind(ToggleButton_IsChecked, nameof(vm.UseResolution), BindingMode.TwoWay);
        var fit = new RadioButton { Content = "Adatta alla finestra", GroupName = "res" }
            .Bind(ToggleButton_IsChecked, nameof(vm.FitResolution), BindingMode.TwoWay)
            .Bind(IsEnabledProperty, nameof(vm.ResolutionEnabled));
        var fix = new RadioButton { Content = "Fissa", GroupName = "res" }
            .Bind(ToggleButton_IsChecked, nameof(vm.FixedResolution), BindingMode.TwoWay)
            .Bind(IsEnabledProperty, nameof(vm.ResolutionEnabled));
        var size = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        size.Children.Add(new TextBox { Width = 100 }.Bind(TextBox.TextProperty, nameof(vm.DesktopWidth), BindingMode.TwoWay).Bind(IsEnabledProperty, nameof(vm.SizeEnabled)));
        size.Children.Add(DialogKit.Label("×"));
        size.Children.Add(new TextBox { Width = 100 }.Bind(TextBox.TextProperty, nameof(vm.DesktopHeight), BindingMode.TwoWay).Bind(IsEnabledProperty, nameof(vm.SizeEnabled)));

        var body = DialogKit.Page(
            DialogKit.Label("Profilo di credenziali per le connessioni della cartella:"), profile,
            DialogKit.Label("Vale per le connessioni senza utente, password o profilo propri, anche nelle sottocartelle.", "muted"),
            use, fit, fix, size,
            DialogKit.Label("Vale per le connessioni che hanno «Usa la risoluzione della cartella» nella loro finestra di modifica.", "muted"),
            DialogKit.Error());
        Content = DialogKit.Frame(new ScrollViewer { Content = body },
            DialogKit.Buttons(
                DialogKit.Button("Annulla", (_, _) => Close(false), isCancel: true),
                DialogKit.Button("OK", (_, _) => { if (vm.TryCommit()) Close(true); }, accent: true, isDefault: true)));
    }

    private static readonly AvaloniaProperty ToggleButton_IsChecked = Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty;
}

/// <summary>PIN di blocco (#28).</summary>
internal sealed class PinSetWindow : ToolWindow
{
    public PinSetWindow(PinSetViewModel vm) : base(vm, vm.WindowTitle, 420, 330, resizable: false)
    {
        var pin = new TextBox { PasswordChar = '●', MaxLength = vm.MaxLength }.Bind(TextBox.TextProperty, nameof(vm.Pin), BindingMode.TwoWay);
        var confirm = new TextBox { PasswordChar = '●', MaxLength = vm.MaxLength }.Bind(TextBox.TextProperty, nameof(vm.ConfirmPin), BindingMode.TwoWay);
        var body = DialogKit.Page(DialogKit.Row("PIN", pin, 80), DialogKit.Row("Ripeti", confirm, 80),
            DialogKit.Label(vm.Note, "muted"), DialogKit.Error());
        Content = DialogKit.Frame(body, DialogKit.Buttons(
            DialogKit.Button("Annulla", (_, _) => Close(false), isCancel: true),
            DialogKit.Button("OK", (_, _) => { if (vm.TryCommit()) Close(true); }, accent: true, isDefault: true)));
        Opened += (_, _) => pin.Focus();
    }
}

/// <summary>Ricerca rapida (#12): campo di testo e elenco, Invio apre, Esc chiude.</summary>
internal sealed class QuickSearchWindow : ToolWindow
{
    public QuickSearchWindow(QuickSearchViewModel vm) : base(vm, "Ricerca rapida", 560, 460)
    {
        var query = new TextBox { Watermark = "Nome, host o etichetta…" }.Bind(TextBox.TextProperty, nameof(vm.Query), BindingMode.TwoWay);
        var list = new ListBox { ItemsSource = vm.Results, Background = Avalonia.Media.Brushes.Transparent }
            .Bind(SelectingItemsControl.SelectedItemProperty, nameof(vm.Selected), BindingMode.TwoWay);
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<QuickSearchItem>((item, _) =>
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 2) };
            var texts = new StackPanel();
            texts.Children.Add(new TextBlock { Text = item?.Name, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis });
            var detail = new TextBlock { Text = item?.Detail, FontSize = 12, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
            detail.Classes.Add("muted");
            texts.Children.Add(detail);
            grid.Children.Add(texts);
            var action = new TextBlock { Text = item?.Action, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center };
            action.Classes.Add("muted");
            Grid.SetColumn(action, 1);
            grid.Children.Add(action);
            return grid;
        });
        list.DoubleTapped += (_, _) => Accept(vm);
        query.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Enter: Accept(vm); e.Handled = true; break;
                case Key.Down: vm.MoveSelection(1); e.Handled = true; break;
                case Key.Up: vm.MoveSelection(-1); e.Handled = true; break;
                case Key.Escape: Close(false); e.Handled = true; break;
            }
        };
        var hint = DialogKit.Label(vm.Hint, "muted");
        hint.Margin = new Thickness(14, 6);
        DockPanel.SetDock(hint, Dock.Bottom);
        query.Margin = new Thickness(12);
        DockPanel.SetDock(query, Dock.Top);
        Content = new DockPanel { Children = { query, hint, list } };
        Opened += (_, _) => query.Focus();
    }

    private void Accept(QuickSearchViewModel vm)
    {
        if (vm.Accept())
            Close(true);
    }
}

/// <summary>Cronologia delle connessioni (#19).</summary>
internal sealed class HistoryWindow : ToolWindow
{
    public HistoryWindow(HistoryViewModel vm) : base(vm, "Cronologia delle connessioni", 900, 520)
    {
        var filter = new ComboBox { ItemsSource = vm.Filters, Width = 300 }
            .Bind(SelectingItemsControl.SelectedItemProperty, nameof(vm.Filter), BindingMode.TwoWay);
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(14) };
        top.Children.Add(DialogKit.Label("Mostra"));
        top.Children.Add(filter);
        top.Children.Add(DialogKit.BoundLabel(nameof(vm.Summary), "muted"));

        var header = Row("Connessione", "Host", "Inizio", "Fine", "Durata", "Motivo della fine", bold: true);
        var list = new ListBox { ItemsSource = vm.Rows, Background = Avalonia.Media.Brushes.Transparent };
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<HistoryRow>((r, _) =>
            Row(r?.Name ?? "", r?.Host ?? "", r?.Start ?? "", r?.End ?? "", r?.Duration ?? "", r?.Reason ?? "", bold: false));
        var body = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);
        body.Children.Add(list);
        var clear = DialogKit.Button("Svuota quelle mostrate", (_, _) => vm.ClearShownCommand.Execute(null));
        Content = DialogKit.Frame(body, DialogKit.Buttons(clear, DialogKit.Button("Chiudi", (_, _) => Close(true), isCancel: true)), top);

        static Grid Row(string a, string b, string c, string d, string e, string f, bool bold)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("190,170,130,130,90,*"), Margin = new Thickness(14, 2) };
            string[] cells = [a, b, c, d, e, f];
            for (int i = 0; i < cells.Length; i++)
            {
                var t = new TextBlock { Text = cells[i], TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
                if (bold)
                    t.FontWeight = Avalonia.Media.FontWeight.SemiBold;
                Grid.SetColumn(t, i);
                g.Children.Add(t);
            }
            return g;
        }
    }
}

/// <summary>Testi rapidi (#17): elenco a sinistra, modifica a destra.</summary>
internal sealed class QuickTextsWindow : ToolWindow
{
    public QuickTextsWindow(QuickTextsViewModel vm) : base(vm, "Testi rapidi", 760, 520)
    {
        var list = new ListBox { ItemsSource = vm.Items, Background = Avalonia.Media.Brushes.Transparent }
            .Bind(SelectingItemsControl.SelectedItemProperty, nameof(vm.Selected), BindingMode.TwoWay);
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<QuickTextItem>((item, _) =>
            new TextBlock { Text = item?.Title, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });

        var side = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        side.Children.Add(Cmd("Nuovo", vm.NewCommand));
        side.Children.Add(Cmd("Elimina", vm.DeleteCommand));
        side.Children.Add(Cmd("Su", vm.MoveUpCommand));
        side.Children.Add(Cmd("Giù", vm.MoveDownCommand));
        DockPanel.SetDock(side, Dock.Bottom);
        var left = new DockPanel { Margin = new Thickness(16, 0, 8, 12), Children = { side, list } };

        var name = new TextBox { Watermark = "Nome (facoltativo)" }.Bind(TextBox.TextProperty, nameof(vm.Name), BindingMode.TwoWay);
        var text = new TextBox { AcceptsReturn = true, AcceptsTab = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinHeight = 200, FontFamily = new Avalonia.Media.FontFamily("monospace") }
            .Bind(TextBox.TextProperty, nameof(vm.Text), BindingMode.TwoWay);
        var save = new Button { Content = "Salva questo testo", HorizontalAlignment = HorizontalAlignment.Left, Command = vm.SaveCommand };
        save.Classes.Add("accent");
        var right = new StackPanel { Spacing = 8, Margin = new Thickness(8, 0, 16, 12) };
        right.Children.Add(DialogKit.Label("Nome (facoltativo)"));
        right.Children.Add(name);
        right.Children.Add(DialogKit.Label("Testo da digitare nella sessione"));
        right.Children.Add(text);
        right.Children.Add(save);
        right.Children.Add(DialogKit.Error());
        right.Bind(IsEnabledProperty, nameof(vm.IsEditing));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*") };
        grid.Children.Add(left);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        var note = DialogKit.Label(vm.Note, "muted");
        note.Margin = new Thickness(16, 12);
        Content = DialogKit.Frame(grid, DialogKit.Buttons(DialogKit.Button("Chiudi", (_, _) => Close(true), isCancel: true)), note);

        static Button Cmd(string text, RdpManager.Linux.ViewModels.RelayCommand c) => new() { Content = text, Command = c, MinWidth = 64 };
    }
}

/// <summary>Esportazione in .rdpm (#24).</summary>
internal sealed class RdpmExportWindow : ToolWindow
{
    public RdpmExportWindow(RdpmExportViewModel vm) : base(vm, vm.WindowTitle, 560, 560)
    {
        var without = new RadioButton { Content = "Senza password", GroupName = "pw" }
            .Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, nameof(vm.WithoutPasswords), BindingMode.TwoWay);
        var with = new RadioButton { Content = "Con le password, cifrate con una password del file", GroupName = "pw" }
            .Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, nameof(vm.WithPasswords), BindingMode.TwoWay);
        var pw = new TextBox { PasswordChar = '●' }.Bind(TextBox.TextProperty, nameof(vm.Password), BindingMode.TwoWay).Bind(IsEnabledProperty, nameof(vm.WithPasswords));
        var again = new TextBox { PasswordChar = '●' }.Bind(TextBox.TextProperty, nameof(vm.ConfirmPassword), BindingMode.TwoWay).Bind(IsEnabledProperty, nameof(vm.WithPasswords));
        var settings = new CheckBox { Content = "Includi le impostazioni dell'app (scorciatoie, tema, aggiornamenti…)" }
            .Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, nameof(vm.IncludeSettings), BindingMode.TwoWay);
        var items = new List<Control> { DialogKit.Label(vm.Summary) };
        if (vm.UnreadableWarning is { } warn)
            items.Add(DialogKit.Label(warn, "error"));
        items.AddRange([without, with, DialogKit.Row("Password del file", pw), DialogKit.Row("Ripeti", again), DialogKit.Label(vm.CipherNote, "muted"),
            settings, DialogKit.Label(vm.SettingsNote, "muted"), DialogKit.Error()]);
        Content = DialogKit.Frame(new ScrollViewer { Content = DialogKit.Page([.. items]) }, DialogKit.Buttons(
            DialogKit.Button("Annulla", (_, _) => Close(false), isCancel: true),
            DialogKit.Button("Esporta…", async (_, _) => { if (await vm.TryAcceptAsync()) Close(true); }, accent: true, isDefault: true)));
    }
}

/// <summary>Importazione da .rdpm (#21).</summary>
internal sealed class RdpmImportWindow : ToolWindow
{
    public RdpmImportWindow(RdpmImportViewModel vm) : base(vm, vm.WindowTitle, 560, 400)
    {
        var merge = new RadioButton { Content = "Unisci: aggiungi solo le connessioni che mancano", GroupName = "mode" }
            .Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, nameof(vm.Merge), BindingMode.TwoWay);
        var replace = new RadioButton { Content = "Sostituisci: le connessioni attuali vengono cancellate (profili e testi rapidi restano)", GroupName = "mode" }
            .Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, nameof(vm.Replace), BindingMode.TwoWay);
        var settings = new CheckBox { Content = "Applica anche le impostazioni dell'app contenute nel file", IsVisible = vm.HasSettings }
            .Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, nameof(vm.ApplySettings), BindingMode.TwoWay);
        Content = DialogKit.Frame(new ScrollViewer { Content = DialogKit.Page(DialogKit.Label(vm.Summary), merge, DialogKit.Label(vm.MergeNote, "muted"), replace, settings) },
            DialogKit.Buttons(
                DialogKit.Button("Annulla", (_, _) => Close(false), isCancel: true),
                DialogKit.Button("Importa", async (_, _) => { if (await vm.TryAcceptAsync()) Close(true); }, accent: true, isDefault: true)));
    }
}

/// <summary>Importazione da CSV (#22): colonne, cartella, anteprima.</summary>
internal sealed class CsvImportWindow : ToolWindow
{
    public CsvImportWindow(CsvImportViewModel vm) : base(vm, vm.WindowTitle, 980, 600)
    {
        var columns = new ItemsControl { ItemsSource = vm.Columns };
        columns.ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new WrapPanel());
        columns.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<CsvColumnViewModel>((col, _) =>
        {
            var p = new StackPanel { Margin = new Thickness(0, 0, 12, 8), Spacing = 2 };
            p.Children.Add(new TextBlock { Text = col?.Header, FontWeight = Avalonia.Media.FontWeight.SemiBold });
            p.Children.Add(new ComboBox { ItemsSource = col?.FieldChoices, MinWidth = 140 }
                .Bind(SelectingItemsControl.SelectedItemProperty, nameof(CsvColumnViewModel.Field), BindingMode.TwoWay));
            return p;
        });
        var folder = new TextBox { Width = 260 }.Bind(TextBox.TextProperty, nameof(vm.Folder), BindingMode.TwoWay);
        var folderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        folderRow.Children.Add(DialogKit.Label("Cartella per le righe senza cartella"));
        folderRow.Children.Add(folder);
        folderRow.Children.Add(DialogKit.BoundLabel(nameof(vm.Summary), "muted"));

        var list = new ListBox { ItemsSource = vm.Preview, Background = Avalonia.Media.Brushes.Transparent };
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<CsvPreviewRow>((r, _) =>
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("50,160,170,55,120,140,*") };
            string[] cells = [r?.Line ?? "", r?.Name ?? "", r?.Host ?? "", r?.Port ?? "", r?.User ?? "", r?.Folder ?? "", r?.Outcome ?? ""];
            for (int i = 0; i < cells.Length; i++)
            {
                var t = new TextBlock { Text = cells[i], TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0), Opacity = r?.CanImport == false ? 0.55 : 1 };
                Grid.SetColumn(t, i);
                g.Children.Add(t);
            }
            return g;
        });
        var top = new StackPanel { Spacing = 8, Margin = new Thickness(14) };
        top.Children.Add(DialogKit.Label(vm.Intro, "muted"));
        top.Children.Add(columns);
        top.Children.Add(folderRow);
        var import = new Button { MinWidth = 96, IsDefault = true };
        import.Classes.Add("accent");
        import.Bind(ContentControl.ContentProperty, nameof(vm.ImportLabel));
        import.Bind(IsEnabledProperty, nameof(vm.CanImport));
        import.Click += (_, _) => Close(true);
        Content = DialogKit.Frame(list, DialogKit.Buttons(DialogKit.Button("Annulla", (_, _) => Close(false), isCancel: true), import), top);
    }
}

/// <summary>Cerca PC in rete (#15): schede Rete locale, Tailscale, Altre VPN.</summary>
internal sealed class NetworkScanWindow : ToolWindow
{
    public NetworkScanWindow(NetworkScanViewModel vm) : base(vm, vm.WindowTitle, 900, 620)
    {
        var tabs = new TabControl { ItemsSource = vm.Tabs };
        tabs.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(vm.SelectedTab), BindingMode.TwoWay));
        tabs.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ScanTabViewModel>((t, _) => new TextBlock { Text = t?.Title });
        tabs.ContentTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ScanTabViewModel>((t, _) => BuildTab(t));

        var folder = new AutoCompleteBox { ItemsSource = vm.Folders, Width = 280, MinimumPrefixLength = 0 }
            .Bind(AutoCompleteBox.TextProperty, nameof(vm.Folder), BindingMode.TwoWay);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(14, 8) };
        bottom.Children.Add(DialogKit.Label("Cartella:"));
        bottom.Children.Add(folder);
        bottom.Children.Add(new Button { Content = "Spunta tutti", Command = vm.CheckAllCommand });
        bottom.Children.Add(new Button { Content = "Aggiungi i selezionati", Command = vm.AddCommand });
        bottom.Children.Add(DialogKit.BoundLabel(nameof(vm.Summary), "muted"));
        DockPanel.SetDock(bottom, Dock.Bottom);
        Content = DialogKit.Frame(tabs, DialogKit.Buttons(DialogKit.Button("Chiudi", (_, _) => Close(true), isCancel: true)), bottom);
        Closing += (_, _) => vm.CancelAll();
    }

    private static Control BuildTab(ScanTabViewModel? tab)
    {
        var top = new StackPanel { Spacing = 8, Margin = new Thickness(10) };
        switch (tab)
        {
            case RangeScanTabViewModel range:
                top.Children.Add(DialogKit.Label(range.Note, "muted"));
                if (range.EmptyNote is { } empty)
                    top.Children.Add(DialogKit.Label(empty, "error"));
                top.Children.Add(DialogKit.Row("Rete", new ComboBox { ItemsSource = range.Networks, HorizontalAlignment = HorizontalAlignment.Stretch }
                    .Bind(SelectingItemsControl.SelectedItemProperty, nameof(range.Network), BindingMode.TwoWay), 90));
                var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                line.Children.Add(DialogKit.Label("Intervallo:"));
                line.Children.Add(new TextBox { Width = 260 }.Bind(TextBox.TextProperty, nameof(range.Range), BindingMode.TwoWay));
                line.Children.Add(DialogKit.Label("Porta:"));
                line.Children.Add(new TextBox { Width = 80 }.Bind(TextBox.TextProperty, nameof(range.Port), BindingMode.TwoWay));
                line.Children.Add(new Button { Content = "Avvia ricerca", Command = range.StartCommand });
                line.Children.Add(new Button { Content = "Annulla", Command = range.CancelCommand });
                top.Children.Add(line);
                top.Children.Add(new ProgressBar { Height = 12 }
                    .Bind(Avalonia.Controls.Primitives.RangeBase.MaximumProperty, nameof(range.ProgressMax))
                    .Bind(Avalonia.Controls.Primitives.RangeBase.ValueProperty, nameof(range.Progress))
                    .Bind(Visual.IsVisibleProperty, nameof(range.IsBusy)));
                break;
            case TailscaleTabViewModel ts:
                top.Children.Add(DialogKit.Label(ts.Note, "muted"));
                var tsLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                tsLine.Children.Add(new Button { Content = "Leggi la rete Tailscale", Command = ts.ReadCommand });
                tsLine.Children.Add(new CheckBox { Content = "Controlla se il Desktop remoto risponde (porta 3389)" }
                    .Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, nameof(ts.CheckRdp), BindingMode.TwoWay));
                top.Children.Add(tsLine);
                break;
        }
        top.Children.Add(DialogKit.BoundLabel(nameof(ScanTabViewModel.Status), "muted"));

        var list = new ListBox { Background = Avalonia.Media.Brushes.Transparent };
        list.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(ScanTabViewModel.Results)));
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<FoundPcItem>((pc, _) =>
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,200,150,60,*,130") };
            var check = new CheckBox { MinHeight = 0 }.Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, nameof(FoundPcItem.IsChecked), BindingMode.TwoWay);
            g.Children.Add(check);
            AddCell(g, 1, nameof(FoundPcItem.Name));
            AddCell(g, 2, nameof(FoundPcItem.Address));
            AddCell(g, 3, nameof(FoundPcItem.Port));
            AddCell(g, 4, nameof(FoundPcItem.Detail));
            AddCell(g, 5, nameof(FoundPcItem.Note));
            return g;
        });
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(list);
        return dock;

        static void AddCell(Grid g, int column, string path)
        {
            var t = DialogKit.BoundLabel(path);
            t.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
            t.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(t, column);
            g.Children.Add(t);
        }
    }
}
