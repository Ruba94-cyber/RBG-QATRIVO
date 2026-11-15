using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TestLoom.App.Services;

namespace TestLoom.App.Views;

/// <summary>
/// Schermata di scelta della versione INTERA: usata quando il confronto campo per campo non e' possibile (file danneggiato,
/// campi non confrontabili) o l'utente preferisce tenere una sola versione. Le due versioni sono in sola lettura e in forma
/// leggibile (mai JSON). Quella non scelta viene salvata nel cestino di recupero: lo dice la schermata. Una versione non
/// leggibile puo' essere scartata ma non scelta.
/// </summary>
public sealed class WholeVersionWindow : Window
{
    public WholeVersionChoice? Scelta { get; private set; }

    private static string T(string it, string en, string fr) => LocalizationService.Instance.Scegli(it, en, fr);

    public WholeVersionWindow(WholeVersionRequest richiesta)
    {
        Title = T("Scegli la versione da tenere", "Choose the version to keep", "Choisir la version à conserver") + " · " + richiesta.Titolo;
        Width = 1020; Height = 700; MinWidth = 640; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetAutomationId(this, "VersioneIntera_Window");
        AdattamentoDialogo.Registra(this, grande: true);

        var testa = new StackPanel { Spacing = 6 };
        testa.Children.Add(new TextBlock { Text = richiesta.Titolo, FontWeight = FontWeight.Bold, FontSize = 18 });
        var motivo = new TextBlock { Text = richiesta.Motivo, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkOrange };
        AutomationProperties.SetAutomationId(motivo, "VersioneIntera_Motivo");
        testa.Children.Add(motivo);
        testa.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = T("Scegli quale versione intera tenere. L'altra NON viene persa: finisce nel cestino di recupero (Impostazioni, Manutenzione) e si può consultare in qualsiasi momento.",
                     "Choose which whole version to keep. The other one is NOT lost: it goes to the recovery bin (Settings, Maintenance) and can be viewed at any time.",
                     "Choisissez la version entière à conserver. L'autre n'est PAS perdue : elle va dans la corbeille de récupération et reste consultable.")
        });

        var colonne = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*"), Margin = new Thickness(0, 12, 0, 0) };
        colonne.Children.Add(Colonna(richiesta.Mia, WholeVersionChoice.TieniMia, "VersioneIntera_Mia"));
        var altra = Colonna(richiesta.Altra, WholeVersionChoice.TieniAltra, "VersioneIntera_Altra");
        Grid.SetColumn(altra, 2);
        colonne.Children.Add(altra);

        var annulla = new Button { Content = T("Annulla (non cambiare nulla)", "Cancel (change nothing)", "Annuler (ne rien changer)"), Classes = { "Secondary" }, HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetAutomationId(annulla, "VersioneIntera_Annulla_Button");
        annulla.Click += (_, _) => Close(null);

        var radice = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(testa, Dock.Top);
        annulla.Margin = new Thickness(0, 10, 0, 0);
        DockPanel.SetDock(annulla, Dock.Bottom);
        radice.Children.Add(testa);
        radice.Children.Add(annulla);
        radice.Children.Add(colonne);
        Content = radice;
    }

    private Control Colonna(WholeVersionSide lato, WholeVersionChoice scelta, string id)
    {
        var titolo = new TextBlock { Text = lato.Etichetta, FontWeight = FontWeight.SemiBold, FontSize = 15 };
        var descr = new TextBlock { Text = lato.Descrizione, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        Control corpo;
        if (lato.Campi is { Count: > 0 })
        {
            // Una riga per campo: le righe diverse dall'altra versione hanno sfondo evidenziato e il segno "≠".
            var righe = new StackPanel { Spacing = 3 };
            foreach (var campo in lato.Campi)
            {
                var contenuto = new StackPanel { Spacing = 1 };
                contenuto.Children.Add(new TextBlock
                {
                    Text = (campo.Diverso ? "≠ " : "") + campo.Etichetta, FontSize = 11,
                    FontWeight = campo.Diverso ? FontWeight.Bold : FontWeight.Normal, Opacity = campo.Diverso ? 1 : 0.65
                });
                contenuto.Children.Add(new TextBlock { Text = campo.Testo, TextWrapping = TextWrapping.Wrap });
                var riga = new Border
                {
                    Child = contenuto, Padding = new Thickness(8, 4), CornerRadius = new CornerRadius(4),
                    Background = campo.Diverso ? new SolidColorBrush(Color.FromArgb(70, 245, 158, 11)) : null,
                    BorderThickness = new Thickness(campo.Diverso ? 3 : 0, 0, 0, 0), BorderBrush = Brushes.DarkOrange
                };
                AutomationProperties.SetAutomationId(riga, id + (campo.Diverso ? "_RigaDiversa" : "_RigaUguale"));
                righe.Children.Add(riga);
            }
            AutomationProperties.SetAutomationId(righe, id + "_Contenuto");
            corpo = new ScrollViewer { Content = righe };
        }
        else
        {
            var testo = new TextBox { Text = lato.Anteprima, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(testo, id + "_Contenuto");
            corpo = testo;
        }
        var tieni = new Button
        {
            Content = T("Tieni questa versione", "Keep this version", "Garder cette version"),
            Classes = { "Primary" }, IsEnabled = lato.Scegliibile, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        AutomationProperties.SetAutomationId(tieni, id + "_Tieni_Button");
        tieni.Click += (_, _) => { Scelta = scelta; Close(scelta); };

        var pannello = new DockPanel();
        var testa = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 8) };
        testa.Children.Add(titolo); testa.Children.Add(descr);
        DockPanel.SetDock(testa, Dock.Top);
        pannello.Children.Add(testa);
        var piede = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0) };
        if (!lato.Scegliibile)
        {
            var avviso = new TextBlock
            {
                Text = (lato.Problema ?? T("Questa versione non si può tenere.", "This version cannot be kept.", "Cette version ne peut pas être conservée.")) + "\n" +
                       T("Puoi scartarla tenendo l'altra versione: quella scartata resta nel cestino di recupero e non si perde. Per recuperarla, correggi il valore indicato e riproponila.",
                         "You can discard it and keep the other version: the discarded one stays in the recovery bin and is not lost. To recover it, fix the indicated value and submit it again.",
                         "Vous pouvez l'écarter et garder l'autre version : celle écartée reste dans la corbeille de récupération. Pour la récupérer, corrigez la valeur indiquée et soumettez-la de nouveau."),
                TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed
            };
            AutomationProperties.SetAutomationId(avviso, id + "_NonLeggibile_Text");
            piede.Children.Add(avviso);
        }
        piede.Children.Add(tieni);
        DockPanel.SetDock(piede, Dock.Bottom);
        pannello.Children.Add(piede);
        pannello.Children.Add(corpo);
        var scheda = new Border { Child = pannello, Padding = new Thickness(12) };
        scheda.Classes.Add("Card");
        return scheda;
    }

    public static async Task<WholeVersionChoice?> MostraAsync(Window owner, WholeVersionRequest richiesta)
    {
        var w = new WholeVersionWindow(richiesta);
        return await w.ShowDialog<WholeVersionChoice?>(owner);
    }
}
