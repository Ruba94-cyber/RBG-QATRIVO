using Avalonia.Controls;

namespace TestLoom.App.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
        AdattamentoDialogo.Registra(this);
        SiButton.Click += (_, _) => Close(true);
        NoButton.Click += (_, _) => Close(false);
    }

    /// <summary>testoNo vuoto nasconde il pulsante No: usato per un avviso a un solo pulsante (es. "OK") invece
    /// di forzare ogni chiamante a inventarsi un secondo testo senza senso per una semplice notifica.</summary>
    public static ConfirmDialog Crea(string titolo, string messaggio, string? testoSi = null, string? testoNo = null)
    {
        var d = new ConfirmDialog { Title = titolo };
        d.MessaggioText.Text = messaggio;
        testoSi ??= Services.LocalizationService.Instance.Traduci("SiLabel");
        testoNo ??= Services.LocalizationService.Instance.Traduci("NoLabel");
        d.SiButton.Content = testoSi;
        d.NoButton.Content = testoNo;
        d.NoButton.IsVisible = !string.IsNullOrEmpty(testoNo);
        return d;
    }
}
