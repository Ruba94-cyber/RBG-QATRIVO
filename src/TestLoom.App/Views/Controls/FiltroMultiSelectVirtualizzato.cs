using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views.Controls;

/// <summary>Componente condiviso di selezione multipla virtualizzata (Addendum performance 2026-09-25, feedback
/// utente: "virtualizzare tutti i filtri in tutte le pagine", 100k Test/NC/WU e fino a ~1000 voci per catalogo).
/// Sostituisce lo schema precedente (uno StackPanel con un CheckBox per OGNI opzione, fino a 1000 controlli
/// creati ad ogni apertura del flyout) con una ListBox: il pannello di default di Avalonia 12 e' un
/// VirtualizingStackPanel, quindi crea/ricicla solo i contenitori realizzati a schermo (qualche decina), non uno
/// per voce. Lo stato di selezione resta nel MODELLO (FiltroOpzione.Selezionato, via binding TwoWay), mai nei
/// controlli: la virtualizzazione ricicla i CheckBox, quindi uno stato "posseduto" dal controllo si
/// perderebbe/mescolerebbe tra voci diverse.
///
/// Ricerca (stessa regola di SelezionaDialog, vedi PickerTesto): sottostringa senza distinzione di maiuscole e di
/// accenti, su TUTTE le M opzioni, con debounce e calcolo fuori dal thread UI tramite PickerRicercaController, cosi'
/// una digitazione non blocca il flyout nemmeno con decine di migliaia di valori. Frecce: dal campo di ricerca
/// giu' porta il cursore nella lista; Spazio o Invio alternano la spunta della voce evidenziata.
///
/// Usato da FiltroMultiSelectFlyoutHelper (Home, Report, Anomalie/CR in NonConformitaView) e riusabile da
/// qualunque altro filtro basato su FiltroMultiSelect.</summary>
public static class FiltroMultiSelectVirtualizzato
{
    /// <summary>Costruisce il contenuto del flyout. <paramref name="onSelezioneCambiata"/> viene invocata dopo
    /// ogni cambio di selezione (click su una voce o su Seleziona/Deseleziona tutto), cosi' il chiamante puo'
    /// aggiornare il testo del pulsante che apre il flyout (TestoRiepilogo).</summary>
    public static Control Costruisci(FiltroMultiSelect filtro, Action? onSelezioneCambiata, int sogliaRicerca)
    {
        var loc = LocalizationService.Instance;
        var ricerca = new FiltroMultiSelectRicerca(filtro);
        var panel = new StackPanel { Spacing = 4, MinWidth = 220, MaxHeight = 340 };

        var azioni = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 2) };
        var selezionaTutto = new Button { Content = loc.Traduci("PickerSelezionaVisibili"), Classes = { "Link" }, Padding = new Thickness(0) };
        var deselezionaTutto = new Button { Content = loc.Traduci("PickerDeselezionaVisibili"), Classes = { "Link" }, Padding = new Thickness(0) };
        AutomationProperties.SetAutomationId(selezionaTutto, "FiltroMultiSelect_SelezionaTutto_Button");
        AutomationProperties.SetAutomationId(deselezionaTutto, "FiltroMultiSelect_DeselezionaTutto_Button");
        azioni.Children.Add(selezionaTutto);
        azioni.Children.Add(deselezionaTutto);
        panel.Children.Add(azioni);

        if (filtro.Opzioni.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = loc.Traduci("NessunaVoceDisponibile"), Classes = { "Muted" } });
            selezionaTutto.IsEnabled = false;
            deselezionaTutto.IsEnabled = false;
            return panel;
        }

        var conteggio = new TextBlock { Classes = { "Muted" }, FontSize = 11 };
        panel.Children.Add(conteggio);

        var listBox = new ListBox
        {
            MaxHeight = 260,
        };
        AutomationProperties.SetAutomationId(listBox, "FiltroMultiSelect_Lista");
        // AutomationId per voce (FiltroMultiSelect_Voce_{Etichetta}): un id impostato sul CheckBox dentro
        // l'ItemTemplate non e' raggiungibile via UIA (Avalonia espone solo l'AutomationPeer del container
        // ListBoxItem generato, vedi README "Una scoperta empirica importante" — qui il contenitore e'
        // impostato via ContainerPrepared perche' l'ItemTemplate e' costruito in codice, non con uno Style
        // XAML su ListBoxItem come altrove nel progetto).
        listBox.ContainerPrepared += (_, e) =>
        {
            if (e.Container is Control { DataContext: FiltroOpzione opzione } container)
                AutomationProperties.SetAutomationId(container, $"FiltroMultiSelect_Voce_{opzione.Etichetta}");
        };
        // La selezione della ListBox ora indica solo il cursore da tastiera; lo stato di spunta resta in
        // FiltroOpzione.Selezionato (CheckBox in binding), quindi evidenziare una riga non spunta nulla.
        listBox.ItemTemplate = new FuncDataTemplate<FiltroOpzione>((opzione, _) =>
        {
            // Il CheckBox occupa l'intera riga: un click ovunque sulla riga cambia la spunta.
            var cb = new CheckBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            cb.Bind(CheckBox.ContentProperty, new Binding(nameof(FiltroOpzione.Etichetta)));
            cb.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(FiltroOpzione.Selezionato)) { Mode = BindingMode.TwoWay });
            // Puo' scattare anche per il ri-binding di un contenitore riciclato (scroll/ricerca): deve restare
            // idempotente (oggi aggiorna solo il testo di riepilogo del pulsante).
            cb.IsCheckedChanged += (_, _) => onSelezioneCambiata?.Invoke();
            return cb;
        }, supportsRecycling: true);

        // Ricerca: debounce, calcolo fuori dal thread UI, scarto dei risultati superati (vedi PickerRicercaController).
        var motore = new PickerMotore<FiltroOpzione>(filtro.Opzioni, o => o.Etichetta ?? "");
        var vociMostrate = motore.Totale;
        IReadOnlyList<FiltroOpzione> risultatiCorrenti = Array.Empty<FiltroOpzione>();
        void Mostra(PickerRisultato<FiltroOpzione> risultato)
        {
            vociMostrate = risultato.Voci.Count;
            risultatiCorrenti = risultato.Voci;
            listBox.ItemsSource = risultato.Voci;
            conteggio.Text = FiltroMultiSelectRicerca.FormattaConteggio(risultato.Voci.Count, risultato.TotaleSorgente);
            selezionaTutto.IsEnabled = true;
            deselezionaTutto.IsEnabled = true;
        }
        var controllo = new PickerRicercaController<FiltroOpzione>(motore, Mostra, PickerRicercaController<FiltroOpzione>.DebounceDefaultMs);
        panel.DetachedFromVisualTree += (_, _) => controllo.Dispose();
        Mostra(motore.Cerca(string.Empty));

        TextBox? ricercaBox = null;
        if (filtro.Opzioni.Count > sogliaRicerca)
        {
            ricercaBox = new TextBox { PlaceholderText = loc.Traduci("CercaPlaceholder"), Margin = new Thickness(0, 0, 0, 4) };
            AutomationProperties.SetAutomationId(ricercaBox, "FiltroMultiSelect_Ricerca_Input");
            ricercaBox.TextChanged += (_, _) =>
            {
                selezionaTutto.IsEnabled = false;
                deselezionaTutto.IsEnabled = false;
                controllo.Digita(ricercaBox.Text);
            };
            ricercaBox.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Down || vociMostrate == 0) return;
                if (listBox.SelectedIndex < 0) listBox.SelectedIndex = 0;
                listBox.Focus();
                e.Handled = true;
            };
            panel.Children.Add(ricercaBox);
        }
        panel.Children.Add(listBox);

        listBox.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Space or Key.Enter && listBox.SelectedItem is FiltroOpzione corrente)
            {
                corrente.Selezionato = !corrente.Selezionato; // la CheckBox aggiorna il riepilogo via IsCheckedChanged
                e.Handled = true;
            }
            else if (e.Key == Key.Up && listBox.SelectedIndex <= 0 && ricercaBox != null)
            {
                ricercaBox.Focus();
                e.Handled = true;
            }
        };

        selezionaTutto.Click += (_, _) => { if (!selezionaTutto.IsEnabled) return; ricerca.SelezionaTutto(risultatiCorrenti); onSelezioneCambiata?.Invoke(); };
        deselezionaTutto.Click += (_, _) => { if (!deselezionaTutto.IsEnabled) return; ricerca.DeselezionaTutto(risultatiCorrenti); onSelezioneCambiata?.Invoke(); };
        return panel;
    }
}
