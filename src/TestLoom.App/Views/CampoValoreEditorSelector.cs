using Avalonia.Controls;
using Avalonia.Controls.Templates;
using TestLoom.App.Models;

namespace TestLoom.App.Views;

/// <summary>Perf 6.4.0 (apertura Test esistente misurata 2,7-3,2s a caldo/18535 visuali, richiesta utente
/// 2026-09-25): in TestCaseEditorView.axaml il DataTemplate di <see cref="CampoStrumentoValore"/> era
/// duplicato 8 volte e OGNI istanza materializzava SEMPRE i controlli di TUTTI i Tipo possibili (TextBox
/// Testo, Grid Numero, StackPanel Lista con ComboBox+Button, ToggleSwitch Checkbox, CalendarDatePicker Data,
/// StackPanel File con 2 TextBlock+3 Button, Button ListaTesto — fino a ~15-19 controlli per singolo campo),
/// nascondendo tutti tranne uno con IsVisible+TipoCampoEqualsConverter. <see cref="CampoStrumentoValore.Tipo"/>
/// è get-only (immutabile per istanza, vedi Models/TestCase.cs), quindi non serve mai ricalcolare quale ramo
/// mostrare: lo si sa una volta sola, alla costruzione del controllo.
///
/// Questo IDataTemplate, assegnato a ContentControl.ContentTemplate dentro il DataTemplate esterno (che
/// mantiene invariati intestazione/Nome/eventuale pulsante "Ricorda come mio default"), sceglie in
/// <see cref="Build"/> UN SOLO sotto-template in base al Tipo del campo e ne costruisce SOLO quello: i
/// controlli degli altri Tipo non vengono mai creati, non solo nascosti. Comportamento identico a prima una
/// volta che il controllo giusto è a schermo (stessi binding/converter/comandi dei sotto-template in
/// UserControl.Resources di TestCaseEditorView.axaml); cambia solo QUANTI controlli vengono costruiti.
///
/// Un Tipo senza il template corrispondente impostato (property null) non mostra nulla, esattamente come
/// prima quando nessun IsVisible risultava vero per quel campo.</summary>
public sealed class CampoValoreEditorSelector : IDataTemplate
{
    public IDataTemplate? TestoTemplate { get; set; }
    public IDataTemplate? NumeroTemplate { get; set; }
    public IDataTemplate? ListaTemplate { get; set; }
    public IDataTemplate? CheckboxTemplate { get; set; }
    public IDataTemplate? DataCampoTemplate { get; set; }
    public IDataTemplate? FileTemplate { get; set; }
    public IDataTemplate? ListaTestoTemplate { get; set; }

    public bool Match(object? data) => data is CampoStrumentoValore;

    public Control? Build(object? param)
    {
        if (param is not CampoStrumentoValore campo) return null;

        var template = campo.Tipo switch
        {
            TipoCampoStrumento.Testo => TestoTemplate,
            TipoCampoStrumento.Numero => NumeroTemplate,
            TipoCampoStrumento.Lista => ListaTemplate,
            TipoCampoStrumento.Checkbox => CheckboxTemplate,
            TipoCampoStrumento.Data => DataCampoTemplate,
            TipoCampoStrumento.File => FileTemplate,
            TipoCampoStrumento.ListaTesto => ListaTestoTemplate,
            _ => null,
        };

        return template?.Build(param);
    }
}
