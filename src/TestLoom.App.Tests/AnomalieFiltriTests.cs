using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>Semantica dei filtri combinati della pagina Anomalie (AND fra criteri, OR dentro una
/// multi-selezione, intervalli di data inclusivi, vuoti) sull'implementazione di riferimento in memoria.</summary>
public sealed class AnomalieFiltriTests
{
    private static NonConformita Nc(string id, PrioritaOperativa p = PrioritaOperativa.Normal, SeveritaAnomalia? s = SeveritaAnomalia.Medium,
        string owner = "Mario", StatoAnomalia w = StatoAnomalia.Confermata, DateTime? apertura = null, DateTime? chiusura = null) => new()
    {
        Id = id, Priorita = p, Severita = s, Owner = owner, StatoWorkflow = w,
        DataApertura = apertura ?? new DateTime(2026, 3, 15, 17, 30, 0), DataChiusura = chiusura,
        Descrizione = "Anomalia " + id
    };

    [Fact]
    public void DurataChiusura_SogliaEsclusivaConMesiDiCalendario()
    {
        // Durata di calendario a granularita' di giorno: l'orario e' ignorato, soglia esclusiva ("maggiore di").
        var apertura = new DateTime(2024, 1, 31, 12, 0, 0);
        var filtro = new AnomalieFiltroNc { DurataMinima = 1, UnitaDurata = UnitaDurataNc.Mesi };
        Assert.False(filtro.Corrisponde(Nc("aperta", apertura: apertura)));
        // 31/01 + 1 mese = 29/02/2024 (anno bisestile): lo stesso giorno non supera, a qualunque ora.
        Assert.False(filtro.Corrisponde(Nc("esatta", apertura: apertura, chiusura: new DateTime(2024, 2, 29, 23, 59, 59))));
        Assert.True(filtro.Corrisponde(Nc("oltre", apertura: apertura, chiusura: new DateTime(2024, 3, 1, 0, 0, 1))));
        Assert.True(filtro.SenzaTesto().Corrisponde(Nc("oltre", apertura: apertura, chiusura: new DateTime(2024, 3, 1, 0, 0, 1))));
        // 31/01/2025 -> 28/02/2025 (AddMonths limita al fine mese).
        var aperturaNonBisestile = new DateTime(2025, 1, 31, 18, 0, 0);
        Assert.False(filtro.Corrisponde(Nc("fm", apertura: aperturaNonBisestile, chiusura: new DateTime(2025, 2, 28, 23, 0, 0))));
        Assert.True(filtro.Corrisponde(Nc("fm+1", apertura: aperturaNonBisestile, chiusura: new DateTime(2025, 3, 1, 1, 0, 0))));
        var settimana = new AnomalieFiltroNc { DurataMinima = 1, UnitaDurata = UnitaDurataNc.Settimane };
        Assert.False(settimana.Corrisponde(Nc("esatta", apertura: apertura, chiusura: apertura.AddDays(7).AddHours(11))));
        Assert.True(settimana.Corrisponde(Nc("oltre", apertura: apertura, chiusura: apertura.Date.AddDays(8))));
        // Coerente con ExportPivotService.GiorniDiVita: "maggiore di N giorni" <=> GiorniDiVita > N.
        var giorni = new AnomalieFiltroNc { DurataMinima = 3, UnitaDurata = UnitaDurataNc.Giorni };
        var chiusura = apertura.Date.AddDays(3).AddHours(23);
        Assert.Equal(3, ExportPivotService.GiorniDiVita(apertura, chiusura, DateTime.Now));
        Assert.False(giorni.Corrisponde(Nc("g3", apertura: apertura, chiusura: chiusura)));
        Assert.True(giorni.Corrisponde(Nc("g4", apertura: apertura, chiusura: apertura.Date.AddDays(4))));
        Assert.False(new AnomalieFiltroNc { DurataMinima = -1 }.Corrisponde(Nc("invalida", apertura: apertura, chiusura: apertura.AddDays(10))));
    }

    [Fact]
    public void CorrezioneChiusura_ValidaETracciaSenzaAlterareLaBaseline()
    {
        var precedente = Nc("NC-1", w: StatoAnomalia.Conclusa, chiusura: new DateTime(2026, 4, 20));
        var candidato = precedente.Clona();
        candidato.DataChiusura = new DateTime(2026, 3, 1);
        Assert.False(AnomaliaAzioniService.RegistraCorrezioneData(candidato, precedente, "operatore"));
        Assert.Empty(candidato.Changelog);
        candidato.DataChiusura = new DateTime(2026, 4, 22);
        Assert.True(AnomaliaAzioniService.RegistraCorrezioneData(candidato, precedente, "operatore"));
        Assert.Equal(new DateTime(2026, 4, 20), precedente.DataChiusura);
        Assert.Contains(candidato.Changelog, c => c.Campo == "Data chiusura" && c.Operatore == "operatore");
    }

    [Fact]
    public void FiltroVuoto_AccettaTutto()
    {
        var f = new AnomalieFiltroNc();
        Assert.True(f.Corrisponde(Nc("NC-1")));
        Assert.True(f.Corrisponde(Nc("NC-2", s: null, owner: "")));
    }

    [Fact]
    public void FiltroPresenzaEsta_DistingueNcConESenzaTicket()
    {
        var conTicket = Nc("NC-TICKET");
        conTicket.CollegamentiPolarion.Add(new CollegamentoPolarion { Codice = "TICKET-123", Url = "https://ticketing.example/TICKET-123" });
        var senzaTicket = Nc("NC-NO-TICKET");

        Assert.True(new AnomalieFiltroNc { HaTicketEsta = true }.Corrisponde(conTicket));
        Assert.False(new AnomalieFiltroNc { HaTicketEsta = true }.Corrisponde(senzaTicket));
        Assert.True(new AnomalieFiltroNc { HaTicketEsta = false }.Corrisponde(senzaTicket));
        Assert.False(new AnomalieFiltroNc { HaTicketEsta = false }.Corrisponde(conTicket));
    }

    [Fact]
    public void FiltroPresenzaEsta_SiMantieneNelPercorsoSenzaTesto()
    {
        var conTicket = new AnomalieFiltroNc { HaTicketEsta = true, Testo = "testo temporaneo" }.SenzaTesto();
        var senzaTicket = Nc("NC-SENZA-TICKET");
        Assert.True(conTicket.Corrisponde(NcConEsta("NC-CON-TICKET")));
        Assert.False(conTicket.Corrisponde(senzaTicket));
    }

    private static NonConformita NcConEsta(string id)
    {
        var nc = Nc(id);
        nc.CollegamentiPolarion.Add(new CollegamentoPolarion { Codice = "TICKET-123", Url = "https://ticketing.example/TICKET-123" });
        return nc;
    }

    [Fact]
    public void MultiSelezione_OrDentroUnCriterio_AndFraCriteri()
    {
        var f = new AnomalieFiltroNc
        {
            Priorita = [PrioritaOperativa.Major, PrioritaOperativa.Critical],
            Gravita = [SeveritaAnomalia.High],
            Assegnatari = ["Mario", "Lucia"]
        };
        Assert.True(f.Corrisponde(Nc("A", PrioritaOperativa.Major, SeveritaAnomalia.High, "Mario")));
        Assert.True(f.Corrisponde(Nc("B", PrioritaOperativa.Critical, SeveritaAnomalia.High, "Lucia")));
        Assert.False(f.Corrisponde(Nc("C", PrioritaOperativa.Minor, SeveritaAnomalia.High, "Mario")));      // priorita' fuori
        Assert.False(f.Corrisponde(Nc("D", PrioritaOperativa.Major, SeveritaAnomalia.Low, "Mario")));       // gravita' fuori
        Assert.False(f.Corrisponde(Nc("E", PrioritaOperativa.Major, SeveritaAnomalia.High, "Giorgio")));    // assegnatario fuori
    }

    [Fact]
    public void Gravita_NonImpostata_NonSoddisfaUnVincoloDiGravita()
    {
        var f = new AnomalieFiltroNc { Gravita = [SeveritaAnomalia.NA] };
        Assert.False(f.Corrisponde(Nc("A", s: null)));
        Assert.True(f.Corrisponde(Nc("B", s: SeveritaAnomalia.NA)));
    }

    [Fact]
    public void StatoWorkflow_MultiSelezione()
    {
        var f = new AnomalieFiltroNc { StatiWorkflow = [StatoAnomalia.InVerifica, StatoAnomalia.Conclusa] };
        Assert.True(f.Corrisponde(Nc("A", w: StatoAnomalia.InVerifica)));
        Assert.True(f.Corrisponde(Nc("B", w: StatoAnomalia.Conclusa)));
        Assert.False(f.Corrisponde(Nc("C", w: StatoAnomalia.Confermata)));
    }

    [Fact]
    public void Stato_ApertaChiusa_DerivatoDalWorkflow()
    {
        var chiusa = new AnomalieFiltroNc { Stato = StatoNonConformita.Chiusa };
        Assert.True(chiusa.Corrisponde(Nc("A", w: StatoAnomalia.Conclusa)));
        Assert.False(chiusa.Corrisponde(Nc("B", w: StatoAnomalia.InVerifica)));
    }

    [Fact]
    public void IntervalloApertura_InclusivoDiEntrambiGliEstremi_IgnoraLOra()
    {
        var f = new AnomalieFiltroNc { AperturaDa = new DateTime(2026, 3, 15), AperturaA = new DateTime(2026, 3, 15) };
        Assert.True(f.Corrisponde(Nc("A", apertura: new DateTime(2026, 3, 15, 0, 0, 0))));
        Assert.True(f.Corrisponde(Nc("B", apertura: new DateTime(2026, 3, 15, 23, 59, 59))));
        Assert.False(f.Corrisponde(Nc("C", apertura: new DateTime(2026, 3, 14, 23, 59, 59))));
        Assert.False(f.Corrisponde(Nc("D", apertura: new DateTime(2026, 3, 16, 0, 0, 0))));
    }

    [Fact]
    public void IntervalloApertura_SoloDa_ESoloA()
    {
        var soloDa = new AnomalieFiltroNc { AperturaDa = new DateTime(2026, 3, 10) };
        Assert.True(soloDa.Corrisponde(Nc("A", apertura: new DateTime(2030, 1, 1))));
        Assert.False(soloDa.Corrisponde(Nc("B", apertura: new DateTime(2026, 3, 9, 23, 0, 0))));
        var soloA = new AnomalieFiltroNc { AperturaA = new DateTime(2026, 3, 10) };
        Assert.True(soloA.Corrisponde(Nc("C", apertura: new DateTime(2020, 1, 1))));
        Assert.False(soloA.Corrisponde(Nc("D", apertura: new DateTime(2026, 3, 11))));
    }

    [Fact]
    public void IntervalloChiusura_NcSenzaDataChiusura_NonCorrispondeMai()
    {
        var f = new AnomalieFiltroNc { ChiusuraDa = new DateTime(2000, 1, 1), ChiusuraA = new DateTime(2100, 1, 1) };
        Assert.False(f.Corrisponde(Nc("A", chiusura: null)));
        Assert.True(f.Corrisponde(Nc("B", chiusura: new DateTime(2026, 5, 1))));
    }

    [Fact]
    public void IntervalloChiusura_InclusivoEAndConApertura()
    {
        var f = new AnomalieFiltroNc
        {
            AperturaDa = new DateTime(2026, 1, 1), AperturaA = new DateTime(2026, 1, 31),
            ChiusuraDa = new DateTime(2026, 2, 1), ChiusuraA = new DateTime(2026, 2, 1)
        };
        Assert.True(f.Corrisponde(Nc("A", apertura: new DateTime(2026, 1, 31, 12, 0, 0), chiusura: new DateTime(2026, 2, 1, 23, 0, 0))));
        Assert.False(f.Corrisponde(Nc("B", apertura: new DateTime(2026, 1, 31), chiusura: new DateTime(2026, 2, 2))));
        Assert.False(f.Corrisponde(Nc("C", apertura: new DateTime(2026, 2, 1), chiusura: new DateTime(2026, 2, 1))));
    }

    [Fact]
    public void Testo_CercaSuCampiDellaNcSenzaDistinzioneDiMaiuscole()
    {
        var nc = Nc("NC-77");
        nc.EcuSegnalate.Add("BCM");
        Assert.True(new AnomalieFiltroNc { Testo = "nc-77" }.Corrisponde(nc));
        Assert.True(new AnomalieFiltroNc { Testo = "bcm" }.Corrisponde(nc));
        Assert.False(new AnomalieFiltroNc { Testo = "zzz" }.Corrisponde(nc));
        Assert.True(new AnomalieFiltroNc { Testo = "   " }.Corrisponde(nc));
    }

    [Fact]
    public void Ambito_UnioneFraTestEScopeProprioDellaNc_AndFraCriteri()
    {
        var tc = new TestCase { Id = "TC-1", Veicolo = "Daily", Componente = new EcuSelection { "Componente-A" } };
        var nc = Nc("NC-1");
        nc.EcuSegnalate.Add("Componente-RETE");
        nc.FunzionalitaSegnalate.Add("F-RETE");

        Assert.True(new AnomalieFiltroAmbito { Componente = new(StringComparer.OrdinalIgnoreCase) { "componente-a" } }.Corrisponde(nc, tc));
        Assert.True(new AnomalieFiltroAmbito { Componente = new(StringComparer.OrdinalIgnoreCase) { "Componente-RETE" } }.Corrisponde(nc, tc));
        Assert.True(new AnomalieFiltroAmbito { Funzionalita = new(StringComparer.OrdinalIgnoreCase) { "F-RETE" } }.Corrisponde(nc, null));
        Assert.False(new AnomalieFiltroAmbito { Funzionalita = new(StringComparer.OrdinalIgnoreCase) { "Altra" } }.Corrisponde(nc, tc));
        var and = new AnomalieFiltroAmbito
        {
            Componente = new(StringComparer.OrdinalIgnoreCase) { "Componente-A" },
            Veicoli = new(StringComparer.OrdinalIgnoreCase) { "NonQuesto" }
        };
        Assert.False(and.Corrisponde(nc, tc));
    }

    [Fact]
    public void Ambito_Gamma_RisoltaAttraversoIlVeicolo()
    {
        var tc = new TestCase { Id = "TC-1", Veicolo = "Daily MY24" };
        var f = new AnomalieFiltroAmbito
        {
            Gamma = new(StringComparer.OrdinalIgnoreCase) { "Daily" },
            GammaDiVeicolo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Daily MY24"] = "Daily" }
        };
        Assert.True(f.Corrisponde(Nc("A"), tc));
        Assert.False(f.Corrisponde(Nc("A"), new TestCase { Id = "TC-2", Veicolo = "Stralis" }));
    }

    [Fact]
    public void Ordinamento_NullsPrimaInCrescente_TieBreakDeterministico()
    {
        var a = Nc("NC-2", s: null);
        var b = Nc("NC-1", s: SeveritaAnomalia.Low);
        var c = Nc("NC-3", s: SeveritaAnomalia.Low);
        var lista = new List<NonConformita> { b, c, a };
        lista.Sort(AnomalieOrdinamentoComparer.Crea(new(AnomalieOrdinamentoChiave.Gravita, false)));
        Assert.Equal(["NC-2", "NC-1", "NC-3"], lista.Select(x => x.Id));
        lista.Sort(AnomalieOrdinamentoComparer.Crea(new(AnomalieOrdinamentoChiave.Gravita, true)));
        Assert.Equal(["NC-3", "NC-1", "NC-2"], lista.Select(x => x.Id));
    }

    [Fact]
    public void ConfrontaNoCase_ComeSqliteNocase_UnderscoreSottoLeLettere()
    {
        Assert.True(AnomalieOrdinamentoComparer.ConfrontaNoCase("a_b", "ab") < 0);
        Assert.True(AnomalieOrdinamentoComparer.ConfrontaNoCase("ABC", "abd") < 0);
        Assert.Equal(0, AnomalieOrdinamentoComparer.ConfrontaNoCase("AbC", "aBc"));
        Assert.True(AnomalieOrdinamentoComparer.ConfrontaNoCase("ab", "abc") < 0);
    }
}

/// <summary>Indice SQLite locale delle NC: equivalenza con l'implementazione di riferimento in memoria (insieme
/// e ordine), inclusa la presenza dei ticket TICKET diretti; migrazione ricostruibile e atomica.</summary>

public sealed class AnomalieIndiceFiltriTests : IDisposable
{
    private readonly string _shared = Path.Combine(Path.GetTempPath(), "TestLoom_NcFiltri_Shared_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_NcFiltri_Local_" + Guid.NewGuid().ToString("N"));

    public AnomalieIndiceFiltriTests()
    {
        Directory.CreateDirectory(_shared);
        Directory.CreateDirectory(_local);
    }

    public void Dispose()
    {
        LocalNonConformitaIndexStore.FaultInjectionMigrazione = null;
        LocalNonConformitaIndexStore.FaultInjectionOpenRead = null;
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_shared, true); } catch { }
        try { Directory.Delete(_local, true); } catch { }
    }

    private static List<NonConformita> Genera(int n, int seed)
    {
        var rnd = new Random(seed);
        var owners = new[] { "Mario", "Lucia", "Giorgio", "Èlena", "", "z_owner" };
        var descr = new[] { "Errore comunicazione CAN", "Perdita di segnale_componente", "Schermo nero all'avvio", "Anomalia àccenti", "abc", "ab_c" };
        var lista = new List<NonConformita>(n);
        for (var i = 0; i < n; i++)
        {
            var chiusa = rnd.Next(3) == 0;
            var nc = new NonConformita
            {
                Uid = Guid.NewGuid(),
                Id = $"NC-{i:D5}",
                Owner = owners[rnd.Next(owners.Length)],
                Priorita = (PrioritaOperativa)rnd.Next(6),
                Severita = rnd.Next(4) == 0 ? null : (SeveritaAnomalia)rnd.Next(6),
                StatoWorkflow = chiusa ? StatoAnomalia.Conclusa : (StatoAnomalia)rnd.Next(0, 5),
                StatoDettaglio = rnd.Next(3) == 0 ? (StatoDettaglioNc)rnd.Next(0, 3) : null,
                DataApertura = new DateTime(2026, 1, 1).AddDays(rnd.Next(120)).AddMinutes(rnd.Next(1440)),
                Descrizione = descr[rnd.Next(descr.Length)] + " " + rnd.Next(10),
                Azione = rnd.Next(2) == 0 ? "In lavorazione" : "",
                TestCaseId = rnd.Next(2) == 0 ? $"TC-{rnd.Next(50):D3}" : "",
            };
            if (chiusa) nc.DataChiusura = nc.DataApertura.AddDays(rnd.Next(60)).AddMinutes(rnd.Next(1440));
            if (rnd.Next(5) == 0) nc.EcuSegnalate.Add("BCM");
            if (rnd.Next(5) == 0) nc.CollegamentiPolarion.Add(new CollegamentoPolarion { Codice = $"TICKET-{i}", Url = $"https://ticketing.example/TICKET-{i}" });
            lista.Add(nc);
        }
        return lista;
    }

    private static AnomalieFiltroNc FiltroCasuale(Random rnd) => new()
    {
        Stato = rnd.Next(4) == 0 ? (StatoNonConformita)rnd.Next(2) : null,
        StatoDettaglio = rnd.Next(8) == 0 ? (StatoDettaglioNc)rnd.Next(0, 3) : null,
        StatiWorkflow = rnd.Next(3) == 0 ? Enumerable.Range(0, rnd.Next(1, 4)).Select(_ => (StatoAnomalia)rnd.Next(0, 5)).Distinct().ToList() : [],
        Priorita = rnd.Next(3) == 0 ? Enumerable.Range(0, rnd.Next(1, 4)).Select(_ => (PrioritaOperativa)rnd.Next(6)).Distinct().ToList() : [],
        Gravita = rnd.Next(3) == 0 ? Enumerable.Range(0, rnd.Next(1, 4)).Select(_ => (SeveritaAnomalia)rnd.Next(6)).Distinct().ToList() : [],
        Assegnatari = rnd.Next(3) == 0 ? new[] { "Mario", "Èlena", "" }.Take(rnd.Next(1, 4)).ToList() : [],
        AperturaDa = rnd.Next(3) == 0 ? new DateTime(2026, 1, 1).AddDays(rnd.Next(120)) : null,
        AperturaA = rnd.Next(3) == 0 ? new DateTime(2026, 1, 1).AddDays(rnd.Next(120)).AddHours(rnd.Next(24)) : null,
        ChiusuraDa = rnd.Next(4) == 0 ? new DateTime(2026, 1, 1).AddDays(rnd.Next(180)) : null,
        ChiusuraA = rnd.Next(4) == 0 ? new DateTime(2026, 1, 1).AddDays(rnd.Next(180)) : null,
        HaTicketEsta = rnd.Next(4) == 0 ? rnd.Next(2) == 0 : null,
        Testo = rnd.Next(4) == 0 ? new[] { "segnale_ecu", "ÀCCENTI", "bcm", "nc-0001", "tc-01", "xyz", "lavorazione" }[rnd.Next(7)] : null,
    };

    [Fact]
    public void Query_EquivalenteAlRiferimentoInMemoria_InsiemeEOrdine()
    {
        var store = new LocalNonConformitaIndexStore(_shared, _local);
        var ncs = Genera(3000, seed: 7);
        var pathPerUid = ncs.ToDictionary(n => n.Uid, n => Path.Combine(_shared, $"nc_{n.Id}.json"));
        store.ReconcileFull(ncs.Select(n => new LocalIndexWriteEntryNc(pathPerUid[n.Uid], DateTime.UtcNow, 1, n)), DateTime.UtcNow);
        var perPath = ncs.ToDictionary(n => pathPerUid[n.Uid]);

        var rnd = new Random(11);
        // Le chiavi che dipendono dal Test collegato non stanno nell'indice: si ordinano in memoria (AnomalieOrdinamentoConTestComparer).
        var chiavi = Enum.GetValues<AnomalieOrdinamentoChiave>().Where(k => !AnomalieOrdinamentoChiaveEstensioni.RichiedeTest(k)).ToArray();
        for (var i = 0; i < 300; i++)
        {
            var filtro = FiltroCasuale(rnd);
            var ord = new AnomalieOrdinamento(chiavi[rnd.Next(chiavi.Length)], rnd.Next(2) == 0);
            var attesi = ncs.Where(n => filtro.Corrisponde(n)).ToList();
            attesi.Sort(AnomalieOrdinamentoComparer.Crea(ord));

            var percorsi = store.QueryPercorsi(filtro, ord, 0, -1);
            Assert.Equal(attesi.Select(n => n.Id), percorsi.Select(p => perPath[p].Id));
            Assert.Equal(attesi.Count, store.Conta(filtro));

            // Pagina intermedia coerente con l'ordine completo.
            var pagina = store.QueryPercorsi(filtro, ord, 5, 10);
            Assert.Equal(attesi.Skip(5).Take(10).Select(n => n.Id), pagina.Select(p => perPath[p].Id));
        }
    }

    [Fact]
    public void NcArchiviata_EsceDalleListeAttive_IndiceEMemoriaConcordano()
    {
        var store = new LocalNonConformitaIndexStore(_shared, _local);
        var attiva = new NonConformita { Uid = Guid.NewGuid(), Id = "NC-A", DataApertura = new DateTime(2026, 3, 1) };
        var archiviata = new NonConformita { Uid = Guid.NewGuid(), Id = "NC-B", DataApertura = new DateTime(2026, 3, 2) };
        archiviata.Archiviata = true;
        store.Upsert(Path.Combine(_shared, "nc_NC-A.json"), DateTime.UtcNow, 1, attiva);
        store.Upsert(Path.Combine(_shared, "nc_NC-B.json"), DateTime.UtcNow, 1, archiviata);

        Assert.Equal(1, store.Conta(new AnomalieFiltroNc()));
        Assert.Equal(2, store.Conta(new AnomalieFiltroNc { IncludiArchiviate = true }));
        Assert.True(new AnomalieFiltroNc().Corrisponde(attiva));
        Assert.False(new AnomalieFiltroNc().Corrisponde(archiviata));
        Assert.True(new AnomalieFiltroNc { IncludiArchiviate = true }.Corrisponde(archiviata));

        // Archiviare dopo un upsert precedente aggiorna la riga (la firma include il flag).
        attiva.Archiviata = true;
        store.Upsert(Path.Combine(_shared, "nc_NC-A.json"), DateTime.UtcNow, 1, attiva);
        Assert.Equal(0, store.Conta(new AnomalieFiltroNc()));
    }

    [Fact]
    public void Upsert_AggiornaLeColonneDiFiltro_RemoveLeToglie()
    {
        var store = new LocalNonConformitaIndexStore(_shared, _local);
        var nc = new NonConformita { Uid = Guid.NewGuid(), Id = "NC-1", Priorita = PrioritaOperativa.Minor, Owner = "A", DataApertura = new DateTime(2026, 2, 1) };
        var path = Path.Combine(_shared, "nc_NC-1.json");
        store.Upsert(path, DateTime.UtcNow, 1, nc);
        Assert.Equal(1, store.Conta(new AnomalieFiltroNc { Priorita = [PrioritaOperativa.Minor] }));

        nc.Priorita = PrioritaOperativa.Critical;
        nc.Owner = "B";
        store.Upsert(path, DateTime.UtcNow, 1, nc);
        Assert.Equal(0, store.Conta(new AnomalieFiltroNc { Priorita = [PrioritaOperativa.Minor] }));
        Assert.Equal(1, store.Conta(new AnomalieFiltroNc { Priorita = [PrioritaOperativa.Critical], Assegnatari = ["B"] }));
        Assert.Equal(["B"], store.AssegnatariDistinti());

        store.Remove(path);
        Assert.Equal(0, store.Conta(new AnomalieFiltroNc()));
    }

    [Fact]
    public void MigrazioneSchemaV3_AggiungeFiltroTicketERileggeIlDatoDalJsonLocale()
    {
        var store = new LocalNonConformitaIndexStore(_shared, _local);
        var conTicket = new NonConformita { Uid = Guid.NewGuid(), Id = "NC-TICKET", DataApertura = new DateTime(2026, 2, 1) };
        conTicket.CollegamentiPolarion.Add(new CollegamentoPolarion { Codice = "TICKET-44", Url = "https://ticketing.example/TICKET-44" });
        var senzaTicket = new NonConformita { Uid = Guid.NewGuid(), Id = "NC-NO-TICKET", DataApertura = new DateTime(2026, 2, 2) };
        var pathCon = Path.Combine(_shared, "nc_NC-TICKET.json");
        var pathSenza = Path.Combine(_shared, "nc_NC-NO-TICKET.json");
        store.ReconcileFull(
        [
            new LocalIndexWriteEntryNc(pathCon, DateTime.UtcNow, 1, conTicket),
            new LocalIndexWriteEntryNc(pathSenza, DateTime.UtcNow, 1, senzaTicket)
        ], DateTime.UtcNow);

        SqliteConnection.ClearAllPools();
        using (var db = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "DROP INDEX ix_nc_filter_ticket_esta; ALTER TABLE nc_filter DROP COLUMN ha_ticket_esta; UPDATE nc_filter SET sig=0; UPDATE meta SET value='3' WHERE key='schema_version';";
            command.ExecuteNonQuery();
        }

        var migrated = new LocalNonConformitaIndexStore(_shared, _local);
        Assert.Equal(1, migrated.Conta(new AnomalieFiltroNc { HaTicketEsta = true }));
        Assert.Equal(1, migrated.Conta(new AnomalieFiltroNc { HaTicketEsta = false }));
        Assert.Equal([pathCon], migrated.QueryPercorsi(new AnomalieFiltroNc { HaTicketEsta = true }, AnomalieOrdinamento.Predefinito, 0, 10));
    }

    // ---------------------------------------------------------------- repository: allineamento e ripiego

    [Fact]
    public void Repository_IndiceAllineatoDopoRefreshESave_ERipiegoCorrettoSeIlDatabaseSiCorrompe()
    {
        var paths = new DataPaths(Path.Combine(_shared, "data"));
        paths.EnsureStructure();
        var store = new LocalNonConformitaIndexStore(paths.Root, _local);
        var repo = new NonConformitaRepository(paths, TimeSpan.Zero, store);
        repo.RefreshIndex();
        Assert.True(repo.IndiceFiltriUtilizzabile);

        var ncConTicket = new NonConformita { Id = "NC-A", Owner = "Mario", Priorita = PrioritaOperativa.Major, DataApertura = new DateTime(2026, 1, 5) };
        ncConTicket.CollegamentiPolarion.Add(new CollegamentoPolarion { Codice = "TICKET-1", Url = "https://ticketing.example/TICKET-1" });
        repo.Save(ncConTicket);
        repo.Save(new NonConformita { Id = "NC-B", Owner = "Lucia", Priorita = PrioritaOperativa.Minor, DataApertura = new DateTime(2026, 1, 6) });
        Assert.True(repo.IndiceFiltriUtilizzabile); // Save incrementale mantiene l'allineamento

        var richiesta = new AnomalieQueryRichiesta(new AnomalieFiltroNc { Priorita = [PrioritaOperativa.Major] },
            new AnomalieFiltroAmbito(), AnomalieOrdinamento.Predefinito, 1, 250);
        var daIndice = AnomalieQueryService.Esegui(repo, _ => null, richiesta);
        Assert.True(daIndice.DaIndice);
        Assert.Equal(["NC-A"], daIndice.Righe.Select(r => r.Nc.Id));

        var conEsta = AnomalieQueryService.Esegui(repo, _ => null,
            richiesta with { Nc = new AnomalieFiltroNc { HaTicketEsta = true } });
        var senzaEsta = AnomalieQueryService.Esegui(repo, _ => null,
            richiesta with { Nc = new AnomalieFiltroNc { HaTicketEsta = false } });
        Assert.True(conEsta.DaIndice);
        Assert.True(senzaEsta.DaIndice);
        Assert.Equal(["NC-A"], conEsta.Righe.Select(r => r.Nc.Id));
        Assert.Equal(["NC-B"], senzaEsta.Righe.Select(r => r.Nc.Id));

        // Una NC cambia fuori dal repository (altro PC) e il refresh non e' ancora avvenuto: l'indice resta
        // comunque coerente con la cache. Dopo la cancellazione del DB locale il percorso in memoria risponde uguale.
        SqliteConnection.ClearAllPools();
        using (var f = new FileStream(store.DatabasePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            f.Write(new byte[100]);
        var dopo = AnomalieQueryService.Esegui(repo, _ => null, richiesta);
        Assert.Equal(["NC-A"], dopo.Righe.Select(r => r.Nc.Id));
        Assert.Equal(1, dopo.Totale);
    }

    [Fact]
    public void Repository_SaveDuranteQuery_CacheModificata_RendeNonAllineatoFinoAlRefresh()
    {
        var paths = new DataPaths(Path.Combine(_shared, "data2"));
        paths.EnsureStructure();
        var repo = new NonConformitaRepository(paths, TimeSpan.Zero, new LocalNonConformitaIndexStore(paths.Root, _local));
        repo.RefreshIndex();
        repo.Save(new NonConformita { Id = "NC-X", Priorita = PrioritaOperativa.Critical });
        // File modificato esternamente + RefreshIndex: l'indice si riallinea e vede il nuovo valore.
        var nc = repo.GetById("NC-X")!;
        nc.Priorita = PrioritaOperativa.Trivial;
        JsonIo.WriteAtomic(repo.FilePathOf(nc), nc, overwrite: true);
        repo.RefreshIndex();
        Assert.True(repo.IndiceFiltriUtilizzabile);
        var r = AnomalieQueryService.Esegui(repo, _ => null, new AnomalieQueryRichiesta(
            new AnomalieFiltroNc { Priorita = [PrioritaOperativa.Trivial] }, new AnomalieFiltroAmbito(), AnomalieOrdinamento.Predefinito, 1, 250));
        Assert.Equal(["NC-X"], r.Righe.Select(x => x.Nc.Id));
    }

    // ---------------------------------------------------------------- regressioni da review

    private NonConformitaRepository NuovoRepo(string nome, out DataPaths paths)
    {
        paths = new DataPaths(Path.Combine(_shared, nome));
        paths.EnsureStructure();
        var repo = new NonConformitaRepository(paths, TimeSpan.Zero, new LocalNonConformitaIndexStore(paths.Root, _local));
        repo.RefreshIndex();
        return repo;
    }

    private static AnomalieQueryRichiesta Tutte() =>
        new(new AnomalieFiltroNc(), new AnomalieFiltroAmbito(), AnomalieOrdinamento.Predefinito, 1, 250);

    [Fact]
    public void F1_SaveConcorrenteDuranteReconcile_IndiceNonPiuValido_NessunDatoVecchio()
    {
        var repo = NuovoRepo("f1a", out var p1a);
        var nc = new NonConformita { Id = "NC-1", Priorita = PrioritaOperativa.Minor, DataApertura = new DateTime(2026, 1, 1) };
        repo.Save(nc);
        Assert.True(repo.IndiceFiltriUtilizzabile);

        JsonIo.WriteAtomic(p1a.NonConformitaFile("NC-EXT"), new NonConformita { Id = "NC-EXT", Uid = Guid.NewGuid() }); // forza un reconcile (cache cambiata)
        // Save che si inserisce fra snapshot e ReconcileFull: il reconcile riscrive il valore VECCHIO.
        repo.PrimaDelReconcileTest = () =>
        {
            var corrente = repo.GetById("NC-1")!;
            corrente.Priorita = PrioritaOperativa.Critical;
            repo.Save(corrente, corrente.RevisioneDati);
            repo.PrimaDelReconcileTest = null;
        };
        repo.RefreshIndex();

        Assert.False(repo.IndiceFiltriUtilizzabile, "indice potenzialmente stantio: non deve essere interrogabile");
        var r = AnomalieQueryService.Esegui(repo, _ => null, new AnomalieQueryRichiesta(
            new AnomalieFiltroNc { Priorita = [PrioritaOperativa.Critical] }, new AnomalieFiltroAmbito(), AnomalieOrdinamento.Predefinito, 1, 250));
        Assert.Equal(["NC-1"], r.Righe.Select(x => x.Nc.Id));   // ripiego in memoria, valore nuovo
        Assert.False(r.DaIndice);

        repo.RefreshIndex(); // il refresh successivo riscrive dallo stato corrente e riallinea
        Assert.True(repo.IndiceFiltriUtilizzabile);
        var r2 = AnomalieQueryService.Esegui(repo, _ => null, new AnomalieQueryRichiesta(
            new AnomalieFiltroNc { Priorita = [PrioritaOperativa.Critical] }, new AnomalieFiltroAmbito(), AnomalieOrdinamento.Predefinito, 1, 250));
        Assert.True(r2.DaIndice);
        Assert.Equal(["NC-1"], r2.Righe.Select(x => x.Nc.Id));
    }

    [Fact]
    public void F1_EliminaConcorrenteDuranteReconcile_NessunaRigaFantasma()
    {
        var repo = NuovoRepo("f1b", out var p1b);
        repo.Save(new NonConformita { Id = "NC-1" });
        repo.Save(new NonConformita { Id = "NC-2" });
        JsonIo.WriteAtomic(p1b.NonConformitaFile("NC-EXT"), new NonConformita { Id = "NC-EXT", Uid = Guid.NewGuid() });
        repo.PrimaDelReconcileTest = () =>
        {
            repo.Elimina(repo.GetById("NC-2")!);
            repo.PrimaDelReconcileTest = null;
        };
        repo.RefreshIndex();

        var r = AnomalieQueryService.Esegui(repo, _ => null, Tutte());
        Assert.Equal(["NC-1", "NC-EXT"], r.Righe.Select(x => x.Nc.Id).OrderBy(x => x));
        repo.RefreshIndex();
        var r2 = AnomalieQueryService.Esegui(repo, _ => null, Tutte());
        Assert.True(r2.DaIndice);
    }

    [Fact]
    public void RefreshConCacheInvariataEIndiceAllineato_NonRiconciliaENonEscludeLIndice()
    {
        // Intervallo di validazione profonda di default (5 min): la validazione profonda rilegge tutti i JSON e
        // produce oggetti nuovi, cioe' una cache "cambiata" a ogni refresh (non e' il caso del refresh ordinario).
        var paths = new DataPaths(Path.Combine(_shared, "skip"));
        paths.EnsureStructure();
        var repo = new NonConformitaRepository(paths, null, new LocalNonConformitaIndexStore(paths.Root, _local));
        repo.RefreshIndex();
        repo.Save(new NonConformita { Id = "NC-1" });
        var chiamate = 0;
        repo.PrimaDelReconcileTest = () => chiamate++;

        for (var i = 0; i < 3; i++)
        {
            repo.RefreshIndex();
            Assert.True(repo.IndiceFiltriUtilizzabile, "nessuna finestra di esclusione a cache invariata");
        }
        Assert.Equal(0, chiamate); // nessun ReconcileFull periodico inutile

        // Un Save concorrente fra due refresh resta gestito dagli upsert incrementali: indice sempre allineato.
        repo.Save(new NonConformita { Id = "NC-2" });
        repo.RefreshIndex();
        Assert.True(repo.IndiceFiltriUtilizzabile);
        var r = AnomalieQueryService.Esegui(repo, _ => null, Tutte());
        Assert.True(r.DaIndice);
        Assert.Equal(2, r.Totale);
    }

    [Fact]
    public void Indice_Degradato_UnReconcileCompletoRiuscitoRipristinaLaSalute()
    {
        var repo = NuovoRepo("salute", out var paths);
        repo.Save(new NonConformita { Id = "NC-1" });
        repo.IndexRecovery!.InvalidaComeObsoleto("upsert scaduto (simulato)");
        Assert.Equal(LocalIndexHealth.Unavailable, repo.IndexRecovery.Salute);
        Assert.False(repo.IndiceFiltriUtilizzabile);

        JsonIo.WriteAtomic(paths.NonConformitaFile("NC-2"), new NonConformita { Id = "NC-2", Uid = Guid.NewGuid() });
        repo.RefreshIndex();

        Assert.Equal(LocalIndexHealth.Healthy, repo.IndexRecovery.Salute);
        Assert.True(repo.IndiceFiltriUtilizzabile);
        Assert.Equal(2, AnomalieQueryService.Esegui(repo, _ => null, Tutte()).Totale);
    }

    [Fact]
    public void OpenRead_PragmaFallito_DisponeLaConnessioneENonLasciaLettureSporche()
    {
        var store = new LocalNonConformitaIndexStore(_shared, _local);
        LocalNonConformitaIndexStore.FaultInjectionOpenRead = () => throw new InvalidOperationException("pragma simulato");
        try { Assert.Throws<InvalidOperationException>(() => store.Conta(new AnomalieFiltroNc())); }
        finally { LocalNonConformitaIndexStore.FaultInjectionOpenRead = null; }

        using var c = new SqliteConnection($"Data Source={store.DatabasePath};Cache=Shared;Pooling=True");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA read_uncommitted;";
        Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
        Assert.Equal(0, store.Conta(new AnomalieFiltroNc())); // lo store resta utilizzabile
    }

    [Fact]
    public void F2_VacuumNonAssociaIlTestoAllaNcSbagliata()
    {
        var store = new LocalNonConformitaIndexStore(_shared, _local);
        var ncs = Enumerable.Range(0, 400).Select(i => new NonConformita
        {
            Uid = Guid.NewGuid(), Id = $"NC-{i:D4}", Descrizione = $"parola{i:D4} uniforme"
        }).ToList();
        string P(NonConformita n) => Path.Combine(_shared, $"nc_{n.Id}.json");
        store.ReconcileFull(ncs.Select(n => new LocalIndexWriteEntryNc(P(n), DateTime.UtcNow, 1, n)), DateTime.UtcNow);
        // Crea buchi nei rowid, poi VACUUM (LocalSqliteMaintenanceService lo esegue su tutti i *.db).
        foreach (var n in ncs.Where((_, i) => i % 3 == 0)) store.Remove(P(n));
        SqliteConnection.ClearAllPools();
        using (var c = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "VACUUM;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        foreach (var n in ncs.Where((_, i) => i % 3 != 0).Take(40))
        {
            var trovati = store.QueryPercorsi(new AnomalieFiltroNc { Testo = $"parola{n.Id[3..]}" }, AnomalieOrdinamento.Predefinito, 0, -1);
            Assert.Equal([P(n)], trovati);
        }
    }

    [Fact]
    public void F2_VecchioNcFilterSenzaFid_VieneRicostruitoDalJson()
    {
        var ncs = Genera(50, seed: 9);
        var dbPath = CreaDatabaseV1(ncs);
        using (var c = new SqliteConnection($"Data Source={dbPath}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE nc_filter(path TEXT PRIMARY KEY, uid TEXT, id TEXT, sig INTEGER, stato_workflow INTEGER, stato INTEGER,
                    stato_dettaglio INTEGER, destinatario INTEGER, priorita INTEGER, severita INTEGER, owner TEXT, apertura_ticks INTEGER,
                    chiusura_ticks INTEGER, descrizione TEXT);
                CREATE TABLE nc_search(fid INTEGER PRIMARY KEY, search_text TEXT);
                INSERT INTO nc_filter(path) VALUES('residuo');
                UPDATE meta SET value='2' WHERE key='schema_version';
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var store = new LocalNonConformitaIndexStore(_shared, _local);

        Assert.Equal(ncs.Count, store.Conta(new AnomalieFiltroNc()));
    }

    [Fact]
    public void F3_MigrazioneConDatabaseOccupatoDaAltroProcesso_AttendeERiesce()
    {
        var ncs = Genera(300, seed: 4);
        var dbPath = CreaDatabaseV1(ncs);
        using var altro = new SqliteConnection($"Data Source={dbPath}");
        altro.Open();
        using (var cmd = altro.CreateCommand())
        {
            cmd.CommandText = "PRAGMA busy_timeout=5000; BEGIN IMMEDIATE;";
            cmd.ExecuteNonQuery();
        }
        var rilascio = Task.Run(() =>
        {
            Thread.Sleep(1800); // oltre il busy_timeout di 750 ms del negozio
            using var c = altro.CreateCommand();
            c.CommandText = "COMMIT;";
            c.ExecuteNonQuery();
        });

        var store = new LocalNonConformitaIndexStore(_shared, _local);
        rilascio.Wait();

        Assert.Equal("4", LeggiSchema(dbPath));
        Assert.Equal(ncs.Count, store.Conta(new AnomalieFiltroNc()));
        Assert.Empty(Directory.GetFiles(store.WorkspaceDirectory, "nc-index.db.corrupt-*"));
    }

    [Fact]
    public void F6_ReadUncommittedNonRestaSullaConnessionePooled()
    {
        var store = new LocalNonConformitaIndexStore(_shared, _local);
        store.Conta(new AnomalieFiltroNc());
        using var c = new SqliteConnection($"Data Source={store.DatabasePath};Cache=Shared;Pooling=True");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA read_uncommitted;";
        Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    [Fact]
    public void F7_RicercaTestuale_IndiceERiferimentoCoincidonoSuCasiUnicodeEsotici()
    {
        var store = new LocalNonConformitaIndexStore(_shared, _local);
        var dati = new[] { "Straße", "İstanbul", "KELVIN", "Kelvin K", "ÀCCENTO", "normale" };
        var ncs = dati.Select((d, i) => new NonConformita { Uid = Guid.NewGuid(), Id = $"NC-{i}", Descrizione = d }).ToList();
        string P(NonConformita n) => Path.Combine(_shared, $"nc_{n.Id}.json");
        store.ReconcileFull(ncs.Select(n => new LocalIndexWriteEntryNc(P(n), DateTime.UtcNow, 1, n)), DateTime.UtcNow);

        foreach (var q in new[] { "strasse", "STRAßE", "i̇stanbul", "istanbul", "İSTANBUL", "kelvin", "Kelvin", "accento", "àccento", "NORM" })
        {
            var f = new AnomalieFiltroNc { Testo = q };
            var attesi = ncs.Where(n => f.Corrisponde(n)).Select(n => n.Id).OrderBy(x => x).ToList();
            var ottenuti = store.QueryPercorsi(f, AnomalieOrdinamento.Predefinito, 0, -1)
                .Select(p => Path.GetFileNameWithoutExtension(p)[3..]).OrderBy(x => x).ToList();
            Assert.Equal(attesi, ottenuti);
        }
    }

    [Fact]
    public void F7_CampiNulliNonLanciano()
    {
        var nc = new NonConformita { Id = "NC-N" };
        nc.Descrizione = null!; nc.Owner = null!; nc.Azione = null!; nc.DescrizioneBreveSegnalata = null!;
        nc.EcuSegnalate = null!; nc.VeicoliSegnalati = null!;
        Assert.False(AnomalieFiltroNc.TestoCorrisponde(nc, "x", null!));
        Assert.True(AnomalieFiltroNc.TestoCorrisponde(nc, "c-n", ""));
        _ = AnomalieFiltroNc.TestoIndicizzato(nc);
        _ = AnomalieIndiceRiga.Da(nc);
        var l = new List<NonConformita> { nc, new() { Id = "NC-A" } };
        l.Sort(AnomalieOrdinamentoComparer.Crea(new(AnomalieOrdinamentoChiave.Descrizione, false)));
    }

    // ---------------------------------------------------------------- migrazione v1 -> v2

    /// <summary>Crea un nc-index.db esattamente come lo scriveva lo schema v1 (senza colonne di filtro).</summary>
    private string CreaDatabaseV1(IReadOnlyList<NonConformita> ncs, bool conRigaCorrotta = false)
    {
        var probe = new LocalNonConformitaIndexStore(_shared, _local); // determina il percorso del DB
        var dbPath = probe.DatabasePath;
        SqliteConnection.ClearAllPools();
        File.Delete(dbPath);
        foreach (var extra in new[] { dbPath + "-wal", dbPath + "-shm" }) if (File.Exists(extra)) File.Delete(extra);

        using (var c = new SqliteConnection($"Data Source={dbPath}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE nc_cache(path TEXT PRIMARY KEY, uid TEXT NOT NULL, id TEXT NOT NULL, revision INTEGER NOT NULL,
                    mtime_utc_ticks INTEGER NOT NULL, file_length INTEGER NOT NULL, content_hash TEXT NOT NULL, json TEXT NOT NULL,
                    indexed_utc_ticks INTEGER NOT NULL);
                CREATE INDEX ix_nc_cache_uid ON nc_cache(uid);
                CREATE INDEX ix_nc_cache_id ON nc_cache(id);
                INSERT INTO meta(key,value) VALUES('schema_version','1'),('full_scan_completed','1');
                """;
            cmd.ExecuteNonQuery();
            using var tx = c.BeginTransaction();
            foreach (var n in ncs)
            {
                using var ins = c.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO nc_cache VALUES($p,$u,$i,1,1,1,'h',$j,1);";
                ins.Parameters.AddWithValue("$p", Path.Combine(_shared, $"nc_{n.Id}.json"));
                ins.Parameters.AddWithValue("$u", n.Uid.ToString("N"));
                ins.Parameters.AddWithValue("$i", n.Id);
                ins.Parameters.AddWithValue("$j", System.Text.Json.JsonSerializer.Serialize(n, JsonIo.CompactOptions));
                ins.ExecuteNonQuery();
            }
            if (conRigaCorrotta)
            {
                using var bad = c.CreateCommand();
                bad.Transaction = tx;
                bad.CommandText = "INSERT INTO nc_cache VALUES('bad','ff','NC-BAD',1,1,1,'h','{non json',1);";
                bad.ExecuteNonQuery();
            }
            tx.Commit();
        }
        SqliteConnection.ClearAllPools();
        return dbPath;
    }

    private static string? LeggiSchema(string dbPath)
    {
        using var c = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key='schema_version';";
        return cmd.ExecuteScalar() as string;
    }

    private static bool HaTabella(string dbPath, string tabella)
    {
        using var c = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n;";
        cmd.Parameters.AddWithValue("$n", tabella);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    [Fact]
    public void MigrazioneV1_PortaLeRigheEsistentiAlloSchemaV2_ConBackfillDalJson()
    {
        var ncs = Genera(500, seed: 3);
        var dbPath = CreaDatabaseV1(ncs, conRigaCorrotta: true);
        Assert.Equal("1", LeggiSchema(dbPath));
        Assert.False(HaTabella(dbPath, "nc_filter"));

        var store = new LocalNonConformitaIndexStore(_shared, _local);

        Assert.Equal("4", LeggiSchema(dbPath));
        Assert.True(HaTabella(dbPath, "nc_filter"));
        Assert.True(store.HasCompletedFullScan);
        var perId = ncs.ToDictionary(n => n.Id);
        var filtro = new AnomalieFiltroNc { Priorita = [PrioritaOperativa.Major, PrioritaOperativa.Critical], Assegnatari = ["Mario", "Lucia"] };
        var attesi = ncs.Where(n => filtro.Corrisponde(n)).Select(n => n.Id).OrderBy(x => x).ToList();
        var ottenuti = store.QueryPercorsi(filtro, new(AnomalieOrdinamentoChiave.Id, false), 0, -1)
            .Select(p => Path.GetFileNameWithoutExtension(p)[3..]).ToList();
        Assert.Equal(attesi, ottenuti);
        // La riga con json corrotto non e' interpretabile: LoadAll la ignora come prima della migrazione.
        Assert.Equal(ncs.Count, store.LoadAll().Count);
    }

    [Fact]
    public void MigrazioneV1_CrashAMeta_RestaV1_ERitentaConSuccessoAlAvvioSuccessivo()
    {
        var ncs = Genera(5000, seed: 5);
        var dbPath = CreaDatabaseV1(ncs);

        LocalNonConformitaIndexStore.FaultInjectionMigrazione = elaborate =>
        {
            if (elaborate >= 2000) throw new InvalidOperationException("crash simulato a meta' migrazione");
        };
        Assert.Throws<InvalidOperationException>(() => new LocalNonConformitaIndexStore(_shared, _local));
        SqliteConnection.ClearAllPools();

        // Transazione annullata: schema e colonne come prima, nessuna riga persa.
        Assert.Equal("1", LeggiSchema(dbPath));
        Assert.False(HaTabella(dbPath, "nc_filter"));

        LocalNonConformitaIndexStore.FaultInjectionMigrazione = null;
        LocalNonConformitaIndexStore.FaultInjectionOpenRead = null;
        var store = new LocalNonConformitaIndexStore(_shared, _local);
        Assert.Equal("4", LeggiSchema(dbPath));
        Assert.Equal(ncs.Count, store.Conta(new AnomalieFiltroNc()));
        Assert.Equal(ncs.Count, store.LoadAll().Count);
    }

    [Fact]
    public void SchemaFuturo_ViaEliminazione_NuovoIndiceVuotoMaUtilizzabile()
    {
        var ncs = Genera(10, seed: 1);
        var dbPath = CreaDatabaseV1(ncs);
        using (var c = new SqliteConnection($"Data Source={dbPath}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE meta SET value='99' WHERE key='schema_version';";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var store = new LocalNonConformitaIndexStore(_shared, _local);

        Assert.False(store.HasCompletedFullScan);
        Assert.Equal(0, store.Conta(new AnomalieFiltroNc()));
        // Versione futura: eliminata e ricreata, nessuna copia .corrupt-* (non e' corruzione).
        Assert.Empty(Directory.GetFiles(store.WorkspaceDirectory, "nc-index.db.corrupt-*"));
    }
}
