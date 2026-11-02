using System.Text;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Regole di ricerca dei selettori con elenchi lunghi (PickerTesto, PickerMotore) e coordinamento di
/// debounce, annullamento e risultati superati (PickerRicercaController). Indipendenti dalla UI: lo stesso motore
/// serve SelezionaDialog e i filtri a selezione multipla.</summary>
public class PickerRicercaTests
{
    private static List<VoceSelezionabile> Voci(int n) =>
        Enumerable.Range(0, n).Select(i => new VoceSelezionabile
        {
            Valore = i,
            TestoPrincipale = $"Voce {i:D4}",
            TestoRicerca = $"Voce {i:D4}",
        }).ToList();

    [Theory]
    [InlineData("Perché", "perche")]
    [InlineData("  Città   ÈTNA  ", "citta etna")]
    [InlineData("Componente_Motore-01", "componente_motore-01")]
    [InlineData("àèìòù", "aeiou")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalizza_MinuscoleSenzaAccentiESpaziRidotti(string? input, string atteso) =>
        Assert.Equal(atteso, PickerTesto.Normalizza(input));

    [Fact]
    public void Corrisponde_SottostringaOvunque_NonSoloPrefisso()
    {
        Assert.True(PickerTesto.Corrisponde(PickerTesto.Normalizza("Centralina motore"), PickerTesto.Normalizza("tore")));
        Assert.True(PickerTesto.Corrisponde(PickerTesto.Normalizza("Frenatura è attiva"), PickerTesto.Normalizza("FRENA")));
        Assert.True(PickerTesto.Corrisponde(PickerTesto.Normalizza("Perché"), PickerTesto.Normalizza("perche")));
        Assert.False(PickerTesto.Corrisponde(PickerTesto.Normalizza("Componente-1"), PickerTesto.Normalizza("componente 1")));
        Assert.True(PickerTesto.Corrisponde("qualunque", string.Empty));
    }

    [Fact]
    public void Cerca_RispettaTestoPrincipaleEDettaglio_SenzaTroncare()
    {
        var voci = Enumerable.Range(0, 1000).Select(i => new VoceSelezionabile
        {
            Valore = i,
            TestoPrincipale = $"Componente-{i:D4}",
            TestoSecondario = i == 777 ? "Centralina Motore" : "Altro",
            TestoRicerca = $"Componente-{i:D4} {(i == 777 ? "Centralina Motore" : "Altro")}",
        }).ToList();
        var motore = new PickerMotore<VoceSelezionabile>(voci, v => v.TestoPrincipale + " " + v.TestoRicerca);

        var perCodice = motore.Cerca("componente-0999");
        Assert.Equal(999, Assert.Single(perCodice.Voci).Valore);

        var perDettaglio = motore.Cerca("MOTORE");
        Assert.Equal(777, Assert.Single(perDettaglio.Voci).Valore);
        Assert.Equal(1000, perDettaglio.TotaleSorgente);
    }

    [Fact]
    public void ChiaveVoce_AggiungeIlCodiceSoloSeNonGiaPresenteInTestoRicerca()
    {
        var conCodice = new VoceSelezionabile { TestoPrincipale = "Componente-1", TestoRicerca = "Componente-1 Motore" };
        var senzaCodice = new VoceSelezionabile { TestoPrincipale = "Componente-2", TestoRicerca = "Motore" };

        Assert.Equal("Componente-1 Motore", PickerTesto.ChiaveVoce(conCodice));
        Assert.Equal("Componente-2 Motore", PickerTesto.ChiaveVoce(senzaCodice));
    }

    [Fact]
    public void Cerca_QueryVuotaOSpazi_RestituisceTutteLeVoci()
    {
        var motore = new PickerMotore<VoceSelezionabile>(Voci(3000), v => v.TestoRicerca);

        Assert.Equal(3000, motore.Cerca(string.Empty).Voci.Count);
        Assert.Equal(3000, motore.Cerca("   ").Voci.Count);
        Assert.Equal(3000, motore.Cerca("   ").TotaleSorgente);
    }

    [Fact]
    public void Cerca_TroveVoceOltreLaPrimaFinestra()
    {
        // 1000 voci: la ricerca deve scandire tutto l'elenco (mai troncarlo), anche per una voce in fondo.
        var motore = new PickerMotore<VoceSelezionabile>(Voci(1000), v => v.TestoRicerca);

        var risultato = motore.Cerca("voce 0999");

        Assert.Equal("Voce 0999", Assert.Single(risultato.Voci).TestoPrincipale);
    }

    /// <summary>Riferimento indipendente (non usa la normalizzazione NFD di PickerTesto): ripiega a mano le lettere
    /// accentate comuni in italiano. Il motore deve dare esattamente lo stesso insieme, nello stesso ordine.</summary>
    private static string Ripiega(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var originale in s)
        {
            var c = char.ToLowerInvariant(originale);
            sb.Append(c switch
            {
                'è' or 'é' => 'e',
                'à' => 'a',
                'ò' or 'ö' => 'o',
                'ù' or 'ü' => 'u',
                'ì' => 'i',
                'ç' => 'c',
                _ => c,
            });
        }
        return sb.ToString();
    }

    [Fact]
    public void Cerca_ParitaConRiferimentoIndipendente_SuDatasetCasuale()
    {
        var rnd = new Random(20260611);
        const string alfabeto = "abcdeèéàòöùüìçABCDE0123-_";
        string Parola() => new string(Enumerable.Range(0, rnd.Next(1, 9)).Select(_ => alfabeto[rnd.Next(alfabeto.Length)]).ToArray());
        var testi = Enumerable.Range(0, 3000).Select(_ => string.Join(' ', Enumerable.Range(0, rnd.Next(1, 5)).Select(_ => Parola()))).ToList();
        var motore = new PickerMotore<string>(testi, t => t);

        for (var k = 0; k < 400; k++)
        {
            var sorgente = testi[rnd.Next(testi.Count)];
            var inizio = rnd.Next(sorgente.Length);
            var query = sorgente.Substring(inizio, Math.Min(rnd.Next(1, 6), sorgente.Length - inizio));
            if (rnd.Next(2) == 0) query = query.ToUpperInvariant();
            if (rnd.Next(3) == 0) query = Ripiega(query);

            var attese = testi.Where(t => Ripiega(t).Contains(Ripiega(query).Trim(), StringComparison.Ordinal)).ToList();
            var ottenute = motore.Cerca(query).Voci;

            Assert.Equal(attese, ottenute);
        }
    }

    [Fact]
    public void Cerca_ConTokenAnnullato_LanciaOperationCanceled()
    {
        var motore = new PickerMotore<VoceSelezionabile>(Voci(10), v => v.TestoRicerca);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => motore.Cerca("voce", cts.Token));
    }

    [Fact]
    public async Task Digita_SoloLUltimaDigitazioneVieneCercataDopoIlDebounce()
    {
        var motore = new PickerMotore<VoceSelezionabile>(Voci(50), v => v.TestoRicerca);
        var attese = new List<TaskCompletionSource>();
        var applicate = new List<string>();
        Func<int, CancellationToken, Task> attesa = (_, ct) =>
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetCanceled(ct));
            attese.Add(tcs);
            return tcs.Task;
        };
        using var controller = new PickerRicercaController<VoceSelezionabile>(
            motore, r => applicate.Add(r.Query), 200, attesa, azione => azione());

        controller.Digita("a");
        controller.Digita("ab");
        controller.Digita("abc");
        Assert.Equal(3, attese.Count);
        attese[2].TrySetResult();
        await controller.UltimaRicerca;

        Assert.Equal(new[] { "abc" }, applicate);
        Assert.True(attese[0].Task.IsCanceled);
        Assert.True(attese[1].Task.IsCanceled);
    }

    [Fact]
    public async Task RisultatoSuperato_NonVieneMostratoDopoUnaDigitazioneSuccessiva()
    {
        var motore = new PickerMotore<VoceSelezionabile>(Voci(50), v => v.TestoRicerca);
        var pendenti = new List<Action>();
        var applicate = new List<string>();
        using var controller = new PickerRicercaController<VoceSelezionabile>(
            motore, r => applicate.Add(r.Query), 0, null, pendenti.Add);

        controller.Digita("voce 1");
        await controller.UltimaRicerca;
        Assert.Single(pendenti);                // risultato "voce 1" pronto, in attesa del thread UI

        controller.Digita("voce 2");            // supera il precedente prima che venga mostrato
        await controller.UltimaRicerca;
        Assert.Equal(2, pendenti.Count);

        foreach (var azione in pendenti.ToList()) azione();
        Assert.Equal(new[] { "voce 2" }, applicate);
    }

    [Fact]
    public async Task Annulla_NonApplicaUnRisultatoGiaPronto()
    {
        var motore = new PickerMotore<VoceSelezionabile>(Voci(50), v => v.TestoRicerca);
        var pendenti = new List<Action>();
        var applicate = new List<string>();
        using var controller = new PickerRicercaController<VoceSelezionabile>(
            motore, r => applicate.Add(r.Query), 0, null, pendenti.Add);

        controller.Digita("voce 5");
        await controller.UltimaRicerca;   // risultato pronto, in attesa del thread UI
        controller.Annulla();             // il selettore mostra subito il risultato per il testo corrente
        foreach (var azione in pendenti.ToList()) azione();

        Assert.Empty(applicate);
    }

    [Fact]
    public async Task Dispose_NonApplicaPiuNullaEIgnoraLeDigitazioniSuccessive()
    {
        var motore = new PickerMotore<VoceSelezionabile>(Voci(50), v => v.TestoRicerca);
        var pendenti = new List<Action>();
        var applicate = new List<string>();
        var controller = new PickerRicercaController<VoceSelezionabile>(
            motore, r => applicate.Add(r.Query), 0, null, pendenti.Add);

        controller.Digita("voce 3");
        await controller.UltimaRicerca;
        controller.Dispose();
        foreach (var azione in pendenti.ToList()) azione();
        controller.Digita("voce 4");
        await controller.UltimaRicerca;

        Assert.Empty(applicate);
        Assert.Single(pendenti);
    }
}
