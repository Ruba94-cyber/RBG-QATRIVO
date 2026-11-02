using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using TestLoom.App.Services;

namespace TestLoom.App.Views;

/// <summary>
/// Picker multi-selezione a soglia (feedback utente 2026-08-21, punti 1/2/3): sotto soglia (10 elementi) un
/// flyout con checkbox + "Seleziona tutto/Deseleziona tutto", sopra soglia un dialogo con ricerca
/// (SelezionaDialogService). Estratto da ChangeRequestEditorView (unico punto d'origine) perché
/// SegnalazioneReteEditorView ha bisogno esattamente dello stesso schema per componente/Veicoli/Funzionalità/Requisiti,
/// senza duplicare ~60 righe di costruzione controlli a runtime tra le due view.
/// </summary>
public static class MultiSelectPickerFlyout
{
    public static async System.Threading.Tasks.Task Apri(object? sender, string titolo, IReadOnlyList<string> disponibili, Action<IReadOnlyList<string>> aggiungi)
    {
        if (sender is not Button button) return;

        if (disponibili.Count > 10)
        {
            var voci = disponibili.Select(v => new VoceSelezionabile { Valore = v, TestoPrincipale = v, TestoRicerca = v }).ToList();
            var scelte = await SelezionaDialogService.ScegliMultipli(titolo, voci);
            if (scelte.Count > 0) aggiungi(scelte.Select(v => (string)v.Valore).ToList());
            return;
        }

        var panel = new StackPanel { Spacing = 4, MinWidth = 220 };
        if (disponibili.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = LocalizationService.Instance.Traduci("NessunaVoceDisponibile"), Classes = { "Muted" } });
            var flyoutVuoto = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
            FlyoutBase.SetAttachedFlyout(button, flyoutVuoto);
            flyoutVuoto.ShowAt(button);
            return;
        }

        var azioni = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 0, 0, 4)
        };
        var checkBoxes = new List<CheckBox>();
        var selezionaTutto = new Button { Content = LocalizationService.Instance.Traduci("PickerSelezionaVisibili"), Classes = { "Link" }, Padding = new Avalonia.Thickness(0) };
        Avalonia.Automation.AutomationProperties.SetAutomationId(selezionaTutto, "MultiSelectPicker_SelectVisible_Button");
        selezionaTutto.Click += (_, _) => { foreach (var c in checkBoxes) c.IsChecked = true; };
        var deselezionaTutto = new Button { Content = LocalizationService.Instance.Traduci("PickerDeselezionaVisibili"), Classes = { "Link" }, Padding = new Avalonia.Thickness(0) };
        Avalonia.Automation.AutomationProperties.SetAutomationId(deselezionaTutto, "MultiSelectPicker_DeselectVisible_Button");
        deselezionaTutto.Click += (_, _) => { foreach (var c in checkBoxes) c.IsChecked = false; };
        azioni.Children.Add(selezionaTutto);
        azioni.Children.Add(deselezionaTutto);
        panel.Children.Add(azioni);

        foreach (var voce in disponibili)
        {
            var check = new CheckBox { Content = voce, IsChecked = false };
            checkBoxes.Add(check);
            panel.Children.Add(check);
        }

        Flyout? flyoutRif = null;
        var conferma = new Button
        {
            Content = LocalizationService.Instance.Traduci("PickerAggiungiSelezionati"),
            Classes = { "Primary" },
            Margin = new Avalonia.Thickness(0, 6, 0, 0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        Avalonia.Automation.AutomationProperties.SetAutomationId(conferma, "MultiSelectPicker_Add_Button");
        conferma.Click += (_, _) =>
        {
            var selezionate = checkBoxes.Where(c => c.IsChecked == true).Select(c => (string)c.Content!).ToList();
            aggiungi(selezionate);
            flyoutRif?.Hide();
        };
        panel.Children.Add(conferma);

        var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
        flyoutRif = flyout;
        FlyoutBase.SetAttachedFlyout(button, flyout);
        flyout.ShowAt(button);
    }
}
