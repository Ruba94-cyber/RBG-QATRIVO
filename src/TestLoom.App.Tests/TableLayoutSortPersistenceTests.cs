using System.ComponentModel;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Ordinamento per colonna salvato per tabella (ogni DataGrid con intestazioni ordinabili): la parte pura di
/// cattura e riapplicazione, il formato su disco e la compatibilita' con i config scritti senza il campo. Il
/// collegamento con Avalonia (evento Sorting, DataGridColumn.Sort) richiede la UI e non e' coperto qui.</summary>
public class TableLayoutSortPersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_TableLayoutOrdinamento", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Cattura_PrendeLaPrimaDescrizione_ComeOrdinamentoPrimario()
    {
        var sort = TableLayoutPreferences.CatturaOrdinamento(new (string?, ListSortDirection)[]
        {
            ("Priorita", ListSortDirection.Descending),
            ("Stato", ListSortDirection.Ascending),
        });

        Assert.NotNull(sort);
        Assert.Equal("Priorita", sort!.Percorso);
        Assert.Equal(ListSortDirection.Descending, sort.Direzione);
    }

    [Fact]
    public void Cattura_SenzaDescrizioni_NonSalvaNulla() =>
        Assert.Null(TableLayoutPreferences.CatturaOrdinamento(Array.Empty<(string?, ListSortDirection)>()));

    [Fact]
    public void Cattura_PrimaDescrizioneSenzaPercorso_NonSalvaNulla()
    {
        // Comparer personalizzato (nessun percorso di proprieta'): non e' ripristinabile per nome di colonna.
        var sort = TableLayoutPreferences.CatturaOrdinamento(new (string?, ListSortDirection)[]
        {
            (null, ListSortDirection.Ascending),
            ("Stato", ListSortDirection.Descending),
        });

        Assert.Null(sort);
    }

    [Fact]
    public void TrovaColonna_UsaSoloLeColonneOrdinabiliConQuelPercorso()
    {
        var colonne = new List<(string? Percorso, bool Ordinabile)>
        {
            ("Stato", false),   // non ordinabile: lo stesso percorso non basta
            (null, true),       // colonna template senza percorso
            ("Stato", true),
        };

        Assert.Equal(2, TableLayoutPreferences.TrovaColonnaOrdinabile(colonne, "Stato"));
        Assert.Equal(-1, TableLayoutPreferences.TrovaColonnaOrdinabile(colonne, "Assegnatario"));
    }

    [Theory]
    [InlineData(true, true, true)]     // ordinamento con percorso su una griglia che consente di ordinare: va riapplicato
    [InlineData(false, true, false)]   // TestCaseList (CanUserSortColumns=false) resta nell'ordine fisso
    [InlineData(true, false, false)]   // nessun ordinamento salvato
    public void DevoApplicare_RispettaGrigliaEOrdinamento(bool ordinamentoConsentito, bool conOrdinamento, bool atteso)
    {
        var sort = conOrdinamento ? new TableSortPreference { Percorso = "Stato" } : null;

        Assert.Equal(atteso, TableLayoutPreferences.DevoApplicareOrdinamento(ordinamentoConsentito, sort));
    }

    [Fact]
    public void DevoApplicare_OrdinamentoSenzaPercorso_NonSiRiapplica() =>
        Assert.False(TableLayoutPreferences.DevoApplicareOrdinamento(true, new TableSortPreference { Percorso = "" }));

    [Fact]
    public void SonoUguali_ConfrontaColonnaEDirezione()
    {
        var asc = new TableSortPreference { Percorso = "Stato", Direzione = ListSortDirection.Ascending };
        var desc = new TableSortPreference { Percorso = "Stato", Direzione = ListSortDirection.Descending };
        var altraColonna = new TableSortPreference { Percorso = "Priorita", Direzione = ListSortDirection.Ascending };

        Assert.True(TableLayoutPreferences.SonoUgualiOrdinamento(null, null));
        Assert.False(TableLayoutPreferences.SonoUgualiOrdinamento(null, asc));
        Assert.True(TableLayoutPreferences.SonoUgualiOrdinamento(asc, new TableSortPreference { Percorso = "Stato" }));
        Assert.False(TableLayoutPreferences.SonoUgualiOrdinamento(asc, desc));
        Assert.False(TableLayoutPreferences.SonoUgualiOrdinamento(asc, altraColonna));
    }

    [Fact]
    public void AppConfig_PersisteOrdinamentoEColonne_E_LoRileggeIdentico()
    {
        var path = Path.Combine(_dir, "config.json");
        var config = new AppConfig();
        config.LayoutTabelle["Mario|Master|anomalie"] = new TableLayoutPreference
        {
            Versione = TableLayoutPreferences.VersioneLayoutCorrente,
            Colonne = [new() { Indice = 0, Ordine = 0, Larghezza = 220, Visibile = true }],
            Ordinamento = new TableSortPreference { Percorso = "Priorita", Direzione = ListSortDirection.Descending },
        };
        config.LayoutTabelle["Mario|Master|functions"] = new TableLayoutPreference
        {
            Ordinamento = new TableSortPreference { Percorso = "Nome", Direzione = ListSortDirection.Ascending },
        };
        new AppConfigService(path).Save(config);

        var letto = new AppConfigService(path).Load();

        var conColonne = letto.LayoutTabelle["Mario|Master|anomalie"];
        Assert.Equal("Priorita", conColonne.Ordinamento!.Percorso);
        Assert.Equal(ListSortDirection.Descending, conColonne.Ordinamento.Direzione);
        Assert.Equal(220, conColonne.Colonne.Single().Larghezza);
        var soloOrdinamento = letto.LayoutTabelle["Mario|Master|functions"];
        Assert.Equal("Nome", soloOrdinamento.Ordinamento!.Percorso);
        Assert.Equal(ListSortDirection.Ascending, soloOrdinamento.Ordinamento.Direzione);
    }

    [Fact]
    public void SenzaOrdinamento_IlCampoNonVieneScritto_QuindiIlFileRestaComePrima()
    {
        var path = Path.Combine(_dir, "config.json");
        var config = new AppConfig();
        config.LayoutTabelle["Mario|Master|componente"] = new TableLayoutPreference
        {
            Versione = TableLayoutPreferences.VersioneLayoutCorrente,
            Colonne = [new() { Indice = 0, Ordine = 0, Larghezza = 180, Visibile = true }],
        };
        new AppConfigService(path).Save(config);

        Assert.DoesNotContain("Ordinamento", File.ReadAllText(path));
        Assert.Null(new AppConfigService(path).Load().LayoutTabelle["Mario|Master|componente"].Ordinamento);
    }

    [Fact]
    public void ConfigSenzaCampoOrdinamento_SiLeggeSenzaOrdinamentoEConservaLeColonne()
    {
        // Formato scritto prima del campo Ordinamento: deve caricarsi con l'ordinamento assente (= nessun ordinamento).
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, """
            {
              "Lingua": "it",
              "LayoutTabelle": {
                "Mario|Master|functions": {
                  "Versione": 2,
                  "Colonne": [ { "Indice": 0, "Ordine": 0, "Larghezza": 220, "Visibile": true } ]
                }
              }
            }
            """);

        var layout = new AppConfigService(path).Load().LayoutTabelle["Mario|Master|functions"];

        Assert.Null(layout.Ordinamento);
        Assert.Equal(220, layout.Colonne.Single().Larghezza);
    }
}
