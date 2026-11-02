using Avalonia.Controls;

namespace TestLoom.App.Views;

public partial class TextEditDialog : Window
{
    public TextEditDialog()
    {
        InitializeComponent();
        AdattamentoDialogo.Registra(this);
        SalvaButton.Click += (_, _) => Close(ContenutoBox.Text ?? "");
        AnnullaButton.Click += (_, _) => Close(null);
    }

    public static TextEditDialog Crea(string titolo, string valoreCorrente)
    {
        var d = new TextEditDialog { Title = titolo };
        d.TitoloText.Text = titolo;
        d.ContenutoBox.Text = valoreCorrente;
        return d;
    }
}
