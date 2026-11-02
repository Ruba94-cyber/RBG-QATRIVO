using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using TestLoom.App.Services;

namespace TestLoom.App.Views;

/// <summary>Scelta delle sezioni da includere nel pacchetto Ticketing, con anteprima live (Addendum §4,
/// feedback QA: prima sempre tutti i campi, senza modo di limitarsi a quelli che servono). Stesso schema di
/// TextEditDialog/ConfirmDialog: finestra dedicata dietro un metodo statico Crea, mostrata con ShowDialog.
/// Non conosce TestCase/AppState direttamente: riceve un generatore di anteprima già pronto, così resta
/// riusabile senza dipendere dal modello dati del chiamante.</summary>
public partial class PolarionSelezioneDialog : Window
{
    private HashSet<string>? _risultato;
    public bool BrowserHandoffRequested { get; private set; }

    public PolarionSelezioneDialog()
    {
        InitializeComponent();
        UiInteractionTraceService.Attach(this, "Test.PolarionPackage");
        AdattamentoDialogo.Registra(this);
        AnnullaButton.Click += (_, _) => Close(null);
        GeneraButton.Click += (_, _) => Close(_risultato);
        PreparaBrowserButton.Click += (_, _) =>
        {
            BrowserHandoffRequested = true;
            Close(_risultato);
        };
    }

    /// <summary>sezioniDisponibili è l'elenco (già filtrato per il Tipo di test del test corrente, vedi
    /// TestTemplate.SezioniPolarionDisponibili) delle chiavi PolarionSezioni da proporre in questo dialogo.
    /// etichettaDi risolve ogni chiave in un testo mostrabile: le chiavi fisse hanno una traduzione dedicata,
    /// le chiavi dinamiche (una per sezione custom del template, feedback utente 2026-08-21 punto 10) non
    /// hanno una chiave di localizzazione pre-registrata — la risoluzione resta quindi responsabilità del
    /// chiamante (TestCaseEditorViewModel.EtichettaSezionePolarion), non più un lookup fisso qui dentro.</summary>
    public static PolarionSelezioneDialog Crea(IEnumerable<string> sezioniDisponibili, IEnumerable<string> sezioniPreselezionate, Func<string, string> etichettaDi, Func<HashSet<string>, string> generaAnteprima)
    {
        var d = new PolarionSelezioneDialog { Title = LocalizationService.Instance.Traduci("PolarionSelezioneTitolo") };
        var attive = new HashSet<string>(sezioniPreselezionate);
        d._risultato = attive;

        void AggiornaAnteprima() => d.AnteprimaBox.Text = generaAnteprima(attive);

        var elencoSezioni = sezioniDisponibili.ToList();
        foreach (var chiave in elencoSezioni)
        {
            var check = new CheckBox { Content = etichettaDi(chiave), IsChecked = attive.Contains(chiave) };
            Avalonia.Automation.AutomationProperties.SetAutomationId(check, $"PolarionSelezione_Sezione_{chiave}_CheckBox");
            check.IsCheckedChanged += (_, _) =>
            {
                if (check.IsChecked == true) attive.Add(chiave); else attive.Remove(chiave);
                AggiornaAnteprima();
            };
            d.SezioniPanel.Children.Add(check);
        }
        // Ricerca solo oltre soglia (feedback utente 2026-08-21), stessa soglia di ListaOltreSogliaConverter:
        // il catalogo fisso resta a 9 voci, ma le sezioni dinamiche per-template (una per SezioneTemplate)
        // possono farlo crescere oltre 10 senza limite.
        if (elencoSezioni.Count > 10)
        {
            d.SezioniRicercaBox.PlaceholderText = LocalizationService.Instance.Traduci("CercaPlaceholder");
            d.SezioniRicercaBox.IsVisible = true;
            d.SezioniRicercaBox.TextChanged += (_, _) =>
            {
                var q = d.SezioniRicercaBox.Text ?? "";
                foreach (var check in d.SezioniPanel.Children.OfType<CheckBox>())
                    check.IsVisible = string.IsNullOrWhiteSpace(q) || (check.Content?.ToString() ?? "").Contains(q, StringComparison.OrdinalIgnoreCase);
            };
        }
        AggiornaAnteprima();
        return d;
    }
}
