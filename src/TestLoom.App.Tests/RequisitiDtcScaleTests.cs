using System.Diagnostics;
using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// Benchmark/test di scala 6.5.0 per i guasti dei requisiti Extra: 1.000 requisiti (300 Extra), 100.000 guasto totali con
/// coda lunga e un requisito da 20.000 (seme fisso). Misura (mediana di 5) byte scritti e tempo di salvataggio,
/// caricamento a freddo del catalogo, apertura schede Requisiti / Requisiti Extra, apertura editor Extra con 100 e
/// 20.000 guasto, riepilogo e drill-down Audit, reindicizzazione master, dimensione del journal. Soglia di accettazione
/// per pagine/aperture: 700 ms (mediana). Scrive il rapporto in .ai/artifacts/benchmark_dtc_scale.txt.
/// </summary>
[Collection("Localization")]
[Trait("Category", "Scale")]
public sealed class RequisitiDtcScaleTests : IDisposable
{
    private const int NumRequisiti = 1000, NumD4d = 300, TotaleDtc = 100_000, DtcGrande = 20_000, Ripetizioni = 5;
    private const double SogliaMs = 700;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_DtcScale_" + Guid.NewGuid().ToString("N"));
    private readonly List<AppState> _stati = new();
    private readonly StringBuilder _rapporto = new();
    private readonly List<string> _violazioni = new();

    public void Dispose()
    {
        foreach (var s in _stati) { try { s.Dispose(); } catch { } }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AppState NuovoStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        _stati.Add(state);
        return state;
    }

    private AppState StatoMaster()
    {
        var state = NuovoStato();
        var ops = state.Master.Operatori.Load().ToList();
        if (!ops.Any(o => o.Nome == "Master Scala"))
        {
            ops.Add(TestOperatori.Sigilla(new Operatore { Nome = "Master Scala", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") }));
            state.Master.Operatori.Save(ops);
        }
        Assert.True(state.ImpostaOperatore("Master Scala", "1234"));
        return state;
    }

    private static string IdReq(int i) => $"REQ-{i:D4}";
    /// <summary>Indici Extra: i primi 300 requisiti (0 = grande da 20.000; 5 = esattamente 100 guasto).</summary>
    private const int IdxGrande = 0, IdxCento = 5;

    private static List<Requisito> Genera()
    {
        var rnd = new Random(20260508);
        var componente = Enumerable.Range(0, 12).Select(i => $"Componente{i:D2}").ToArray();
        // Coda lunga sui restanti Extra (escluso grande e cento): pesi 1/(rango+1).
        var altri = Enumerable.Range(1, NumD4d - 1).Where(i => i != IdxCento).ToList();
        var resto = TotaleDtc - DtcGrande - 100;
        var pesi = altri.Select((_, k) => 1.0 / (k + 1)).ToArray();
        var somma = pesi.Sum();
        var conteggi = pesi.Select(p => Math.Max(1, (int)Math.Floor(resto * p / somma))).ToArray();
        var diff = resto - conteggi.Sum();
        for (var k = 0; diff > 0; k = (k + 1) % conteggi.Length, diff--) conteggi[k]++;
        for (var k = 0; diff < 0; k = (k + 1) % conteggi.Length) if (conteggi[k] > 1) { conteggi[k]--; diff++; }
        var perIdx = new Dictionary<int, int> { [IdxGrande] = DtcGrande, [IdxCento] = 100 };
        for (var k = 0; k < altri.Count; k++) perIdx[altri[k]] = conteggi[k];

        var lista = new List<Requisito>(NumRequisiti);
        for (var i = 0; i < NumRequisiti; i++)
        {
            var r = new Requisito { Id = IdReq(i), Testo = $"Testo del requisito {i} " + new string('x', 60), ApplicabileBanco = i % 2 == 0, ApplicabileVeicolo = true, ModificatoDa = "Seed", UltimaModifica = new DateTime(2026, 5, 1) };
            if (i < NumD4d)
            {
                RequisitoApplicabilita.ImpostaTipo(r, TipoRequisito.Extra);
                var n = perIdx[i];
                var voci = new List<DtcRequisito>(n);
                for (var k = 0; k < n; k++) voci.Add(new DtcRequisito { Codice = $"P{(i * 977 + k):X4}{rnd.Next(0, 16):X}", Componente = componente[(i + k) % componente.Length] });
                if (n > 0) r.Guasto = voci;
            }
            lista.Add(r);
        }
        Assert.Equal(TotaleDtc, lista.Sum(r => r.DtcVoci.Count));
        return lista;
    }

    private static long DimensioneCartella(string dir) =>
        Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Where(f => !f.Name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)).Sum(f => f.Length) : 0;

    private static double Mediana(IEnumerable<double> v) { var a = v.OrderBy(x => x).ToArray(); return a[a.Length / 2]; }

    private double Misura(string nome, Action azione, double? soglia = null, Action? prepara = null)
    {
        var t = new List<double>();
        for (var i = 0; i < Ripetizioni; i++)
        {
            prepara?.Invoke();
            var sw = Stopwatch.StartNew();
            azione();
            sw.Stop();
            t.Add(sw.Elapsed.TotalMilliseconds);
        }
        var m = Mediana(t);
        _rapporto.AppendLine($"{nome}: mediana {m:F0} ms (min {t.Min():F0}, max {t.Max():F0}){(soglia.HasValue ? $" soglia {soglia:F0}" : "")}");
        if (soglia.HasValue && m > soglia.Value) _violazioni.Add($"{nome}: {m:F0} ms > {soglia:F0} ms");
        return m;
    }

    private async Task<(double Ms, long Byte)> MisuraSalvataggio(string nome, Action prepara, Func<Task> salva, double? sogliaMs, long? sogliaByte)
    {
        var tempi = new List<double>(); var byt = new List<double>();
        for (var i = 0; i < Ripetizioni; i++)
        {
            prepara();
            var prima = DimensioneCartella(_dir);
            var sw = Stopwatch.StartNew();
            await salva();
            sw.Stop();
            tempi.Add(sw.Elapsed.TotalMilliseconds);
            byt.Add(DimensioneCartella(_dir) - prima);
        }
        var m = Mediana(tempi); var b = (long)Mediana(byt);
        _rapporto.AppendLine($"{nome}: mediana {m:F0} ms, byte scritti {b:N0} (min {tempi.Min():F0}, max {tempi.Max():F0} ms)");
        if (sogliaMs.HasValue && m > sogliaMs.Value) _violazioni.Add($"{nome}: {m:F0} ms > {sogliaMs:F0} ms");
        if (sogliaByte.HasValue && b > sogliaByte.Value) _violazioni.Add($"{nome}: {b:N0} byte > {sogliaByte:N0}");
        return (m, b);
    }

    [Fact]
    public async Task Requisiti1000_Guasto100k_MisureESoglie700ms()
    {
        var dati = Genera();
        var master = StatoMaster();
        var swSeed = Stopwatch.StartNew();
        // Formato 6.5.0: guasto nell'archivio separato (un documento per requisito), requisito con solo il conteggio.
        var daArchiviare = dati.Where(r => r.Guasto != null).Select(r => (r, voci: r.Guasto!)).ToList();
        foreach (var (r, voci) in daArchiviare) { r.Guasto = null; r.DtcConteggio = voci.Count; }
        master.Master.Requisiti.Save(dati);
        master.Master.RequisitiDtc.Scrivi(daArchiviare.Select(x => new DtcScrittura(x.r, x.voci, null)).ToList(), "Seed");
        swSeed.Stop();
        _rapporto.AppendLine($"Seed 1000 requisiti / {TotaleDtc} guasto: {swSeed.ElapsedMilliseconds} ms; cartella condivisa {DimensioneCartella(_dir):N0} byte");

        // (a) salvataggio di UNA modifica a UN requisito senza guasto (Standard), via editor.
        {
            using var vm = new RequisitiViewModel(master, TipoRequisito.Standard);
            var idStd = IdReq(NumD4d + 10);
            await MisuraSalvataggio("(a) Salva modifica requisito senza guasto (IniziaModifica escluso)",
                () => { vm.IniziaModificaCommand.AttendiModifica(); Assert.True(vm.InModifica); vm.Items.Single(x => x.Id == idStd).Testo += "."; },
                async () => { await vm.SalvaCommand.ExecuteAsync(null); Assert.False(vm.InModifica, vm.MessaggioErrore); }, 300, 1_000_000);
            Misura("(a2) IniziaModifica editor Standard (promozione fotografia)", () => { vm.IniziaModificaCommand.AttendiModifica(); }, SogliaMs, () => { if (vm.InModifica) vm.AnnullaCommand.Execute(null); });
            if (vm.InModifica) vm.AnnullaCommand.Execute(null);
        }

        // (b)/(d) Extra con 100 guasto e con 20.000 guasto: aggiunta di 1 guasto.
        using (var vmD = new RequisitiViewModel(master, TipoRequisito.Extra))
        {
            foreach (var (idx, etichetta) in new[] { (IdxCento, "100 guasto"), (IdxGrande, "20000 guasto") })
            {
                var id = IdReq(idx); var contatore = 0;
                await MisuraSalvataggio($"(b) Salva +1 guasto a requisito Extra con {etichetta}",
                    () =>
                    {
                        vmD.IniziaModificaCommand.AttendiModifica(); Assert.True(vmD.InModifica);
                        vmD.ElementoSelezionato = vmD.Items.Single(x => x.Id == id);
                        vmD.DtcCaricamento.GetAwaiter().GetResult();
                        vmD.NuovoDtcCodice = $"U{Interlocked.Increment(ref contatore)}{idx}A"; vmD.NuovoDtcEcu = "Componente01";
                        vmD.AggiungiDtcCommand.Execute(null);
                    },
                    async () => { await vmD.SalvaCommand.ExecuteAsync(null); Assert.False(vmD.InModifica, vmD.MessaggioErrore); }, null, null);
            }
            // Apertura editor: modifica avviata + requisito selezionato = lista guasto pronta (VM; la virtualizzazione e' nella view).
            foreach (var (idx, etichetta) in new[] { (IdxCento, "100 guasto"), (IdxGrande, "20000 guasto") })
            {
                var id = IdReq(idx);
                Misura($"(d) Apertura editor Extra con {etichetta} (IniziaModifica + selezione)", () =>
                {
                    vmD.IniziaModificaCommand.AttendiModifica();
                    vmD.ElementoSelezionato = vmD.Items.Single(x => x.Id == id);
                    vmD.DtcCaricamento.GetAwaiter().GetResult();
                    Assert.True(vmD.DtcVisibili.Count >= 100);
                }, SogliaMs, () => { if (vmD.InModifica) vmD.AnnullaCommand.Execute(null); });
                if (vmD.InModifica) vmD.AnnullaCommand.Execute(null);
                Misura($"(d2) Selezione requisito Extra con {etichetta} in sola lettura", () =>
                {
                    vmD.ElementoSelezionato = null;
                    var r = vmD.Items.FirstOrDefault(x => x.Id == id);
                    if (r != null) { vmD.ElementoSelezionato = r; vmD.DtcCaricamento.GetAwaiter().GetResult(); }
                }, SogliaMs);
            }
        }

        // (c) caricamento a freddo: nuova istanza AppState (riavvio, read-model locale presente).
        Misura("(c1) Requisiti.Load() nuova sessione (read-model locale presente)", () =>
        {
            var s = NuovoStato();
            var l = s.Master.Requisiti.Load();
            Assert.Equal(NumRequisiti, l.Count);
        }, null);
        Misura("(c2) Apertura scheda Requisiti Extra (prima pagina) nuova sessione", () =>
        {
            var s = NuovoStato();
            using var vm = new RequisitiViewModel(s, TipoRequisito.Extra);
            Assert.NotEmpty(vm.Items);
        }, SogliaMs);
        Misura("(c3) Apertura scheda Requisiti Standard (prima pagina) nuova sessione", () =>
        {
            var s = NuovoStato();
            using var vm = new RequisitiViewModel(s, TipoRequisito.Standard);
            Assert.NotEmpty(vm.Items);
        }, SogliaMs);
        {
            var s = StatoMaster();
            Misura("(c4) Ritorno alla scheda Requisiti Extra (stessa sessione)", () =>
            {
                using var vm = new RequisitiViewModel(s, TipoRequisito.Extra);
                Assert.NotEmpty(vm.Items);
            }, SogliaMs);
        }

        // (c0) primo avvio su un PC senza read-model locale (journal intero da materializzare): informativo + soglia 1,5 s.
        {
            var coord = new MasterDataLocalIndexCoordinator(StatoMaster().Paths);
            var dirLocale = coord.Store.WorkspaceDirectory;
            Misura("(c0) Primo avvio senza read-model locale: Requisiti.Load() + prima pagina Extra", () =>
            {
                var s0 = NuovoStato();
                Assert.Equal(NumRequisiti, s0.Master.Requisiti.Load().Count);
                using var vm0 = new RequisitiViewModel(s0, TipoRequisito.Extra);
                Assert.NotEmpty(vm0.Items);
            }, 1500, () =>
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var f in Directory.EnumerateFiles(dirLocale)) { try { File.Delete(f); } catch { } }
            });
        }

        // (e) Audit: riepilogo k/n Extra e primo drill-down (stesso percorso del VM, eseguito qui come in background).
        {
            var s = StatoMaster();
            Misura("(e1) Audit Extra: riepilogo k/n per requisito", () => { var r = DtcRiepilogoD4dForTest(s); Assert.True(r >= NumD4d - 1); }, SogliaMs);
            var vmA = new AuditViewModel(s);
            foreach (var (idx, etichetta) in new[] { (IdxCento, "100 guasto"), (IdxGrande, "20000 guasto") })
            {
                var id = IdReq(idx); var t = new List<double>();
                for (var i = 0; i < Ripetizioni; i++)
                {
                    var sw = Stopwatch.StartNew();
                    await vmA.CaricaDettaglioDtcAsync(id);
                    sw.Stop(); t.Add(sw.Elapsed.TotalMilliseconds);
                }
                var m = Mediana(t);
                _rapporto.AppendLine($"(e2) Audit primo drill-down guasto ({etichetta}): mediana {m:F0} ms (soglia {SogliaMs})");
                if (m > SogliaMs) _violazioni.Add($"(e2) drill-down {etichetta}: {m:F0} ms");
                Assert.True(vmA.DtcRighe.Count >= 100);
            }
            Misura("(e3) Costruzione AuditViewModel (apertura pagina Audit)", () => { _ = new AuditViewModel(s); }, SogliaMs);
        }

        // (f) reindicizzazione master dopo il salvataggio.
        {
            var s = StatoMaster();
            var coord = new MasterDataLocalIndexCoordinator(s.Paths);
            Misura("(f) Reindicizzazione master requisiti dopo salvataggio (RefreshRequisiti)", () => coord.RefreshRequisiti(), null,
                () =>
                {
                    var l = s.Master.Requisiti.Load();
                    l.First(x => x.Id == IdReq(NumD4d + 20)).Testo += "!";
                    s.Master.Requisiti.Save(l);
                });
        }

        // (h) Pagina Test: apertura editor di un test collegato a un Extra (100 e 20.000 guasto), selettore guasto pronto.
        {
            var s = StatoMaster();
            foreach (var (idx, etichetta, id) in new[] { (IdxCento, "100 guasto", "T-D100"), (IdxGrande, "20000 guasto", "T-D20K") })
            {
                var tc = new TestCase { Id = id, Componente = new EcuSelection(new[] { "Componente05" }), Veicolo = "V", Veicoli = { "V" }, Funzionalita = { "F1" } };
                tc.RequisitoId.Add(IdReq(idx));
                tc.Esecuzioni.Add(new Esecuzione { Iterazione = 1, Data = DateTime.Now, Esito = Esito.DaEseguire, Matricola = "x", Condizioni = { new Condizione { Esito = Esito.DaEseguire } } });
                s.TestCases.Save(tc, true);
                Misura($"(h) Apertura editor Test collegato a Extra con {etichetta} (selettore guasto)", () =>
                {
                    var vmT = new TestCaseEditorViewModel(s, id, () => { }, _ => { });
                    Assert.True(vmT.MostraDtcOperazione);
                    Assert.True(vmT.DtcUsaPicker || vmT.DtcOpzioniPiccole.Count > 0);
                }, SogliaMs);
            }
        }

        // (g) dimensioni
        var files = new DirectoryInfo(_dir).EnumerateFiles("*", SearchOption.AllDirectories).OrderByDescending(f => f.Length).Take(3);
        _rapporto.AppendLine($"(g) Cartella condivisa totale {DimensioneCartella(_dir):N0} byte; file maggiori: " + string.Join(", ", files.Select(f => $"{f.Name}={f.Length:N0}")));

        var dirArtifacts = TrovaArtifacts();
        var testo = _rapporto.ToString();
        if (dirArtifacts != null) File.AppendAllText(Path.Combine(dirArtifacts, "benchmark_dtc_scale.txt"), $"[{DateTime.UtcNow:O}]{Environment.NewLine}{testo}{Environment.NewLine}");
        Assert.True(_violazioni.Count == 0, string.Join(" | ", _violazioni) + Environment.NewLine + testo);
    }

    /// <summary>L3 (review 6.5.0): Risolvi() a freddo e dopo una scrittura guasto di un altro PC, con suggerimento DtcConteggio assente
    /// (caso che costringe al conteggio leggero dagli archivi). Mediana di 5; il risultato e riportato in benchmark_dtc_scale.txt.
    /// Soglia dura 700 ms; obiettivo UI &lt; 100 ms (verificato con SogliaUiMs).</summary>
    [Fact]
    public void Risolvi_ConteggiLeggeri_Guasto100k_MedianaSottoSoglia()
    {
        var dati = Genera();
        var master = StatoMaster();
        var daArchiviare = dati.Where(r => r.Guasto != null).Select(r => (r, voci: r.Guasto!)).ToList();
        foreach (var (r, _) in daArchiviare) r.Guasto = null;      // DtcConteggio resta null: suggerimento assente/stantio
        master.Master.Requisiti.Save(dati);
        master.Master.RequisitiDtc.Scrivi(daArchiviare.Select(x => new DtcScrittura(x.r, x.voci, null)).ToList(), "Seed");

        const double SogliaUiMs = 100;
        var freddo = new List<double>(); var dopoScrittura = new List<double>(); var rilettura = new List<double>();
        var altroPc = StatoMaster();
        for (var i = 0; i < Ripetizioni; i++)
        {
            var s = NuovoStato();
            var extra = s.Master.Requisiti.Load().Where(RequisitoApplicabilita.ED4D).ToList();
            var sw = Stopwatch.StartNew();
            s.Master.RequisitiDtc.Risolvi(extra);
            sw.Stop(); freddo.Add(sw.Elapsed.TotalMilliseconds);
            Assert.Equal(DtcGrande + i, extra.Single(r => r.Id == IdReq(IdxGrande)).DtcTotale);

            // seconda lettura nella stessa sessione, oggetti nuovi: deve usare la cache (impronta invariata)
            var extra2 = s.Master.Requisiti.Load().Where(RequisitoApplicabilita.ED4D).ToList();
            sw.Restart(); s.Master.RequisitiDtc.Risolvi(extra2); sw.Stop(); rilettura.Add(sw.Elapsed.TotalMilliseconds);

            // un altro PC aggiunge un guasto al requisito grande: la sessione s deve vedere 20.001 dopo l'invalidazione
            var rg = altroPc.Master.Requisiti.Load().Single(r => r.Id == IdReq(IdxGrande));
            var b = altroPc.Master.RequisitiDtc.Leggi(rg);
            var voci = b.Voci.Append(new DtcRequisito { Codice = $"ZZ{i}", Componente = "Componente01" }).ToList();
            altroPc.Master.RequisitiDtc.Scrivi(new[] { new DtcScrittura(rg, voci, b.Documento) }, "AltroPC");
            var extra3 = s.Master.Requisiti.Load().Where(RequisitoApplicabilita.ED4D).ToList();
            sw.Restart(); s.Master.RequisitiDtc.Risolvi(extra3); sw.Stop(); dopoScrittura.Add(sw.Elapsed.TotalMilliseconds);
            Assert.Equal(DtcGrande + i + 1, extra3.Single(r => r.Id == IdReq(IdxGrande)).DtcTotale);
        }
        var testo = $"(i) Risolvi 300 Extra / 100k guasto: a freddo mediana {Mediana(freddo):F0} ms (max {freddo.Max():F0}); " +
                    $"rilettura in cache mediana {Mediana(rilettura):F0} ms; dopo scrittura altro PC mediana {Mediana(dopoScrittura):F0} ms (max {dopoScrittura.Max():F0}); soglia {SogliaMs} ms, obiettivo UI {SogliaUiMs} ms";
        var dirArtifacts = TrovaArtifacts();
        if (dirArtifacts != null) File.AppendAllText(Path.Combine(dirArtifacts, "benchmark_dtc_scale.txt"), $"[{DateTime.UtcNow:O}]{Environment.NewLine}{testo}{Environment.NewLine}");
        Assert.True(Mediana(freddo) <= SogliaMs && Mediana(dopoScrittura) <= SogliaMs, testo);
        Assert.True(Mediana(freddo) <= SogliaUiMs && Mediana(dopoScrittura) <= SogliaUiMs, "Oltre l'obiettivo UI: " + testo);
    }

    /// <summary>Stesso calcolo di AuditViewModel.CaricaDtcD4dAsync (percorso di produzione, vedi DtcRiepilogoD4dProvider).</summary>
    private static int DtcRiepilogoD4dForTest(AppState s)
    {
        var reqs = s.Master.Requisiti.Load().Where(r => !r.Archiviata && RequisitoApplicabilita.ED4D(r) && r.DtcTotale > 0).ToList();
        var ris = DtcAvanzamentoProvider.CalcolaRiepilogo(s, reqs, default);
        return ris.PerRequisito.Count;
    }

    private static string? TrovaArtifacts()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var c = Path.Combine(dir.FullName, ".ai", "artifacts");
            if (Directory.Exists(c)) return c;
            dir = dir.Parent;
        }
        return null;
    }
}
