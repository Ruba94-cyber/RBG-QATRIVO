using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views;

/// <summary>Pannello di correzione manuale dello stato del Report (6.6.1, fase 2): elenco delle combinazioni sottostanti a una cella,
/// ciascuna con esito, propagazione del "!" dalla fonte e rimozione del valore manuale. Motivazione obbligatoria. Solo Master: per gli
/// altri ruoli il pannello e' in sola lettura. Costruito nel codice perche' il pannello dipende dalle righe della cella scelta.</summary>
public sealed class CorreggiStatoWindow : Window
{
    private readonly ReportViewModel _vm;
    private readonly List<(RigaCorrezioneStato Riga, ComboBox Esito, CheckBox? Propaga)> _controlli = new();
    private readonly TextBox _nota;
    private readonly TextBlock _messaggio;
    private readonly Button _salva;

    public CorreggiStatoWindow(ReportViewModel vm, string titolo, IReadOnlyList<RigaCorrezioneStato> righe)
    {
        _vm = vm;
        var master = vm.PuoCorreggereStato;
        Title = titolo;
        Width = 660;
        Height = 600;
        MinWidth = 480;
        MinHeight = 400;
        AutomationProperties.SetAutomationId(this, "CorreggiStato_Window");

        var corpo = new StackPanel { Spacing = 10, Margin = new Thickness(18) };
        corpo.Children.Add(new TextBlock { Text = titolo, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        corpo.Children.Add(new TextBlock { Text = Testo("MatriceCorreggiSottotitolo"), Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
        if (!master)
            corpo.Children.Add(new TextBlock { Text = Testo("MatriceCorreggiSoloLettura"), Foreground = Brushes.DarkOrange, TextWrapping = TextWrapping.Wrap });

        var elenco = new StackPanel { Spacing = 10 };
        for (var i = 0; i < righe.Count; i++)
        {
            var riga = righe[i];
            var blocco = new StackPanel { Spacing = 4 };
            var veicolo = string.IsNullOrEmpty(riga.Veicolo) ? "—" : riga.Veicolo;
            blocco.Children.Add(new TextBlock
            {
                Text = $"{riga.Funzionalita} › {riga.Componente} › {veicolo}  ·  {EtichettaStato(riga.Stato)}",
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            var comandi = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var esito = new ComboBox
            {
                Width = 240,
                IsEnabled = master,
                ItemsSource = new[] { Testo("MatriceCorreggiInvariato"), Esito.Conforme.Label(), Esito.NonConforme.Label(), Esito.ConformeConDeviazioni.Label() },
                SelectedIndex = 0,
            };
            AutomationProperties.SetAutomationId(esito, $"CorreggiStato_Esito_{i}");
            comandi.Children.Add(esito);
            CheckBox? propaga = null;
            if (riga.Propagabile && riga.Fonte != null)
            {
                propaga = new CheckBox
                {
                    Content = $"{Testo("MatriceCorreggiPropaga")} — {string.Format(Testo("MatriceCorreggiFonte"), riga.Fonte.VeicoloSorgente, riga.Fonte.EsitoSorgente.Label())}",
                    IsEnabled = master,
                };
                AutomationProperties.SetAutomationId(propaga, $"CorreggiStato_Propaga_{i}");
                comandi.Children.Add(propaga);
            }
            if (riga.ManualeVincente || riga.ManualeSuperato)
            {
                var rimuovi = new Button { Content = Testo("MatriceCorreggiRimuovi"), IsEnabled = master };
                AutomationProperties.SetAutomationId(rimuovi, $"CorreggiStato_Rimuovi_{i}");
                var riga0 = riga;
                rimuovi.Click += (_, _) => RimuoviRiga(riga0);
                comandi.Children.Add(rimuovi);
            }
            blocco.Children.Add(comandi);
            elenco.Children.Add(blocco);
            _controlli.Add((riga, esito, propaga));
            esito.SelectionChanged += (_, _) => AggiornaSalva();
            if (propaga != null) propaga.IsCheckedChanged += (_, _) => AggiornaSalva();
        }
        corpo.Children.Add(new ScrollViewer { Content = elenco, MaxHeight = 360 });

        corpo.Children.Add(new TextBlock { Text = Testo("MatriceCorreggiNota"), FontWeight = FontWeight.SemiBold });
        _nota = new TextBox { AcceptsReturn = true, Height = 70, PlaceholderText = Testo("MatriceCorreggiNota"), IsEnabled = master };
        AutomationProperties.SetAutomationId(_nota, "CorreggiStato_Nota_TextBox");
        _nota.TextChanged += (_, _) => AggiornaSalva();
        corpo.Children.Add(_nota);
        _messaggio = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
        corpo.Children.Add(_messaggio);

        _salva = new Button { Content = Testo("MatriceCorreggiSalva"), IsEnabled = false };
        AutomationProperties.SetAutomationId(_salva, "CorreggiStato_Salva_Button");
        _salva.Click += (_, _) => Salva();
        var annulla = new Button { Content = Testo("MatriceCorreggiAnnulla") };
        AutomationProperties.SetAutomationId(annulla, "CorreggiStato_Annulla_Button");
        annulla.Click += (_, _) => Close(false);
        corpo.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { annulla, _salva } });

        Content = new ScrollViewer { Content = corpo };
        AggiornaSalva();
    }

    private static string Testo(string chiave) => LocalizationService.Instance.Traduci(chiave);

    /// <summary>Etichetta dello stato con la stessa resa della cella (testo di StatiMatrice, localizzato).</summary>
    private static string EtichettaStato(StatoCombinazioneMatrice stato) => (stato switch
    {
        StatoCombinazioneMatrice.Conforme => new StatiMatrice(1, 0, 0, 0, 0, 0),
        StatoCombinazioneMatrice.NonConforme => new StatiMatrice(0, 1, 0, 0, 0, 0),
        StatoCombinazioneMatrice.ConformeConDeviazioni => new StatiMatrice(0, 0, 1, 0, 0, 0),
        StatoCombinazioneMatrice.DaEseguire => new StatiMatrice(0, 0, 0, 1, 0, 0),
        StatoCombinazioneMatrice.DaDecidere => new StatiMatrice(0, 0, 0, 0, 0, 1),
        _ => new StatiMatrice(0, 0, 0, 0, 1, 0),
    }).Testo;

    /// <summary>Salvataggio abilitato solo con motivazione e almeno una scelta (Master).</summary>
    private void AggiornaSalva()
    {
        var scelta = _controlli.Any(c => c.Esito.SelectedIndex > 0 || c.Propaga?.IsChecked == true);
        _salva.IsEnabled = _vm.PuoCorreggereStato && scelta && !string.IsNullOrWhiteSpace(_nota.Text);
    }

    private void Salva()
    {
        var scelte = new List<CorrezioneRigaStato>();
        foreach (var (riga, esito, propaga) in _controlli)
        {
            var propagare = propaga?.IsChecked == true && riga.Fonte != null;
            Esito? manuale = propagare ? null : esito.SelectedIndex switch
            {
                1 => Esito.Conforme,
                2 => Esito.NonConforme,
                3 => Esito.ConformeConDeviazioni,
                _ => null,
            };
            if (manuale == null && !propagare) continue;
            scelte.Add(new CorrezioneRigaStato(riga.Funzionalita, riga.Componente, riga.Veicolo, manuale, propagare, propagare ? riga.Fonte : null));
        }
        if (scelte.Count == 0)
        {
            _messaggio.Text = Testo("MatriceCorreggiNessunaScelta");
            return;
        }
        if (!_vm.SalvaCorrezioneStato(scelte, _nota.Text ?? "", out var errore))
        {
            _messaggio.Text = errore ?? "";
            return;
        }
        Close(true);
    }

    private void RimuoviRiga(RigaCorrezioneStato riga)
    {
        if (!_vm.RimuoviInserimentoManuale(new[] { riga }, out var errore))
        {
            _messaggio.Text = errore ?? "";
            return;
        }
        Close(true);
    }
}
