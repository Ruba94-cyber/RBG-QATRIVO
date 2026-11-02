using System.Collections.ObjectModel;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

public class NavOrderServiceTests
{
    private static NavItem Item(string id) => new() { Id = id, Titolo = id, Icona = "x", Crea = () => null! };

    [Fact]
    public void Ordina_RispettaLOrdineSalvato()
    {
        var items = new List<NavItem> { Item("a"), Item("b"), Item("c") };

        var risultato = NavOrderService.Ordina(items, new List<string> { "c", "a", "b" });

        Assert.Equal(new[] { "c", "a", "b" }, risultato.Select(i => i.Id));
    }

    [Fact]
    public void Ordina_VoceNonInOrdineSalvatoRestaInFondoNellOrdineOriginale()
    {
        var items = new List<NavItem> { Item("a"), Item("b"), Item("c") };

        var risultato = NavOrderService.Ordina(items, new List<string> { "b" });

        Assert.Equal(new[] { "b", "a", "c" }, risultato.Select(i => i.Id));
    }

    [Fact]
    public void Ordina_OrdineSalvatoVuoto_RestituisceOrdineOriginale()
    {
        var items = new List<NavItem> { Item("a"), Item("b") };

        var risultato = NavOrderService.Ordina(items, new List<string>());

        Assert.Equal(new[] { "a", "b" }, risultato.Select(i => i.Id));
    }

    [Fact]
    public void Ordina_IdSalvatoCheNonEsistePiuVieneIgnorato()
    {
        var items = new List<NavItem> { Item("a"), Item("b") };

        var risultato = NavOrderService.Ordina(items, new List<string> { "b", "zombie", "a" });

        Assert.Equal(new[] { "b", "a" }, risultato.Select(i => i.Id));
    }

    [Fact]
    public void Muovi_Su_ScambiaConLElementoPrecedente()
    {
        var collezione = new ObservableCollection<NavItem> { Item("a"), Item("b"), Item("c") };

        NavOrderService.Muovi(collezione, 1, -1);

        Assert.Equal(new[] { "b", "a", "c" }, collezione.Select(i => i.Id));
    }

    [Fact]
    public void Muovi_Giu_ScambiaConLElementoSuccessivo()
    {
        var collezione = new ObservableCollection<NavItem> { Item("a"), Item("b"), Item("c") };

        NavOrderService.Muovi(collezione, 1, 1);

        Assert.Equal(new[] { "a", "c", "b" }, collezione.Select(i => i.Id));
    }

    [Fact]
    public void Muovi_PrimoElementoSu_NonHaEffetto()
    {
        var collezione = new ObservableCollection<NavItem> { Item("a"), Item("b") };

        NavOrderService.Muovi(collezione, 0, -1);

        Assert.Equal(new[] { "a", "b" }, collezione.Select(i => i.Id));
    }

    [Fact]
    public void Muovi_UltimoElementoGiu_NonHaEffetto()
    {
        var collezione = new ObservableCollection<NavItem> { Item("a"), Item("b") };

        NavOrderService.Muovi(collezione, 1, 1);

        Assert.Equal(new[] { "a", "b" }, collezione.Select(i => i.Id));
    }
}
