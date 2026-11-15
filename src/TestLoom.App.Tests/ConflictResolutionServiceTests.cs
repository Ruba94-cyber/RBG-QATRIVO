using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Risoluzione end-to-end di una copia di conflitto NC: confronto grafico, scelta versione intera, versione corrotta, annullamento.
/// Garanzie: nessuna scrittura prima della conferma; la versione scartata/incorporata e' nel recovery-bin PRIMA del salvataggio;
/// il conflitto si chiude solo dopo il salvataggio; la guardia non blocca piu' i salvataggi successivi.
/// </summary>
[Collection("ConflictResolverUi")]
public sealed class ConflictResolutionServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_ConflictRes_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        ConflictResolverUi.MergeHandler = null;
        ConflictResolverUi.WholeVersionHandler = null;
        foreach (var path in new[] { _dir, FailedSaveDraftService.LocalDirectory(_dir) })
            if (Directory.Exists(path)) { try { Directory.Delete(path, true); } catch { } }
    }

    private AppState CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Op Master", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { op });
        Assert.True(state.ImpostaOperatore(op.Nome, "1234"));
        return state;
    }

    private static (string Path, NonConformita Nc) SalvaNc(AppState state)
    {
        state.NonConformita.Save(new NonConformita
        {
            Id = "NC-CR-1", Owner = "Anna", Descrizione = "Descrizione base", Severita = SeveritaAnomalia.Medium,
            StatoWorkflow = StatoAnomalia.Confermata, DataApertura = DateTime.Now
        });
        var nc = state.NonConformita.GetById("NC-CR-1")!;
        return (state.NonConformita.FilePathOf(nc), nc);
    }

    private static string ScriviCopia(string canonico, NonConformita baseNc, Action<NonConformita> modifica, string suffisso = " (Laptop)")
    {
        var altra = EntityStateFingerprint.Clone(baseNc);
        modifica(altra);
        var copia = canonico.Replace(".json", suffisso + ".json", StringComparison.OrdinalIgnoreCase);
        JsonIo.WriteAtomic(copia, altra);
        return copia;
    }

    private static ConflictCopySource Ritira(AppState state, string copia)
    {
        Assert.Equal(1, ConflictCopyQuarantineService.Run(state.Paths, "Op Master").Ritirate);
        Assert.False(File.Exists(copia));
        return ConflictCopySource.FromEntry(Assert.Single(ConflictCopyQuarantineService.List(state.Paths.Root)));
    }

    [Fact]
    public async Task Unione_SalvaIlRisultato_ArchiviaEntrambeLeVersioni_ChiudeIlConflitto_ESbloccaLeScritture()
    {
        using var state = CreaStato();
        var (path, nc) = SalvaNc(state);
        var copia = ScriviCopia(path, nc, n => { n.Owner = "Bruno"; n.Descrizione = "Descrizione migliorata"; });
        var src = Ritira(state, copia);
        Assert.Throws<IOException>(() => state.NonConformita.Save(state.NonConformita.GetById("NC-CR-1")!.Clona(), nc.RevisioneDati));

        MergeUiRequest? visto = null;
        ConflictResolverUi.MergeHandler = req =>
        {
            visto = req;
            var scelte = new RecordMergeChoices();
            scelte.Scelte["Owner"] = RecordFieldChoice.Mia;
            scelte.Scelte["Descrizione"] = RecordFieldChoice.Loro;
            return Task.FromResult(new MergeUiResult(MergeUiEsito.Unisci, scelte));
        };

        var esito = await ConflictResolutionService.RisolviCopiaAsync(state, src);

        Assert.True(esito.Risolto);
        Assert.NotNull(visto);
        Assert.All(visto!.Plan.Fields, f => Assert.DoesNotContain("{", f.MiaTesto + f.LoroTesto));
        state.NonConformita.RefreshPaths(new[] { path });
        var dopo = state.NonConformita.GetById("NC-CR-1")!;
        Assert.Equal("Anna", dopo.Owner);
        Assert.Equal("Descrizione migliorata", dopo.Descrizione);
        // Il merge e' un salvataggio normale: la revisione avanza di uno (gli altri PC vedono la modifica) e il file lo riflette.
        Assert.Equal(nc.RevisioneDati + 1, dopo.RevisioneDati);
        Assert.Equal(dopo.RevisioneDati, System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path)).RootElement.GetProperty("RevisioneDati").GetInt64());
        Assert.Empty(ConflictCopyQuarantineService.List(state.Paths.Root));
        var bin = DomainRecoveryService.GetAll(state.Paths).Where(r => r.VersioneScartata).ToList();
        Assert.Equal(2, bin.Count); // versione in conflitto + versione corrente prima dell'unione
        Assert.Contains(bin, r => Encoding.UTF8.GetString(Convert.FromBase64String(r.PayloadBase64)).Contains("Bruno"));
        // I salvataggi successivi non sono piu' bloccati.
        var ancora = dopo.Clona();
        ancora.Owner = "Dopo";
        state.NonConformita.Save(ancora, ancora.RevisioneDati);
    }

    [Fact]
    public async Task Annulla_NonModificaNulla_ConflittoResta()
    {
        using var state = CreaStato();
        var (path, nc) = SalvaNc(state);
        var copia = ScriviCopia(path, nc, n => n.Owner = "Bruno");
        var src = Ritira(state, copia);
        var prima = File.ReadAllBytes(path);
        ConflictResolverUi.MergeHandler = _ => Task.FromResult(new MergeUiResult(MergeUiEsito.Annulla, null));

        var esito = await ConflictResolutionService.RisolviCopiaAsync(state, src);

        Assert.False(esito.Risolto);
        Assert.Equal(prima, File.ReadAllBytes(path));
        Assert.Single(ConflictCopyQuarantineService.List(state.Paths.Root));
        Assert.Empty(DomainRecoveryService.GetAll(state.Paths));
    }

    [Fact]
    public async Task VersioneIntera_TieniLaLoro_SostituisceIlCanonico_ELaMiaVaNelRecoveryBin()
    {
        using var state = CreaStato();
        var (path, nc) = SalvaNc(state);
        var copia = ScriviCopia(path, nc, n => { n.Owner = "Bruno"; n.Descrizione = "Versione del collega"; });
        var src = Ritira(state, copia);
        ConflictResolverUi.MergeHandler = _ => Task.FromResult(new MergeUiResult(MergeUiEsito.VersioneIntera, null));
        WholeVersionRequest? req = null;
        ConflictResolverUi.WholeVersionHandler = r => { req = r; return Task.FromResult<WholeVersionChoice?>(WholeVersionChoice.TieniAltra); };

        var esito = await ConflictResolutionService.RisolviCopiaAsync(state, src);

        Assert.True(esito.Risolto);
        Assert.True(req!.Mia.Scegliibile && req.Altra.Scegliibile);
        Assert.DoesNotContain("{", req.Mia.Anteprima + req.Altra.Anteprima);
        state.NonConformita.RefreshPaths(new[] { path });
        Assert.Equal("Bruno", state.NonConformita.GetById("NC-CR-1")!.Owner);
        Assert.Contains(DomainRecoveryService.GetAll(state.Paths),
            r => r.VersioneScartata && Encoding.UTF8.GetString(Convert.FromBase64String(r.PayloadBase64)).Contains("Anna"));
        Assert.Empty(ConflictCopyQuarantineService.List(state.Paths.Root));
    }

    [Fact]
    public async Task VersioneIntera_TieniLaMia_ConservaIlCanonico_ELaLoroVaNelRecoveryBin()
    {
        using var state = CreaStato();
        var (path, nc) = SalvaNc(state);
        var copia = ScriviCopia(path, nc, n => n.Owner = "Bruno");
        var src = Ritira(state, copia);
        var prima = File.ReadAllBytes(path);
        ConflictResolverUi.MergeHandler = _ => Task.FromResult(new MergeUiResult(MergeUiEsito.VersioneIntera, null));
        ConflictResolverUi.WholeVersionHandler = _ => Task.FromResult<WholeVersionChoice?>(WholeVersionChoice.TieniMia);

        var esito = await ConflictResolutionService.RisolviCopiaAsync(state, src);

        Assert.True(esito.Risolto);
        Assert.Equal(prima, File.ReadAllBytes(path));
        var bin = Assert.Single(DomainRecoveryService.GetAll(state.Paths), r => r.VersioneScartata);
        Assert.Contains("Bruno", Encoding.UTF8.GetString(Convert.FromBase64String(bin.PayloadBase64)));
        Assert.Empty(ConflictCopyQuarantineService.List(state.Paths.Root));
    }

    [Fact]
    public async Task CopiaCorrotta_SchermataVersioneIntera_SolaLettura_ScartaEArchiviaIByte()
    {
        using var state = CreaStato();
        var (path, nc) = SalvaNc(state);
        var copia = path.Replace(".json", " (Laptop).json", StringComparison.OrdinalIgnoreCase);
        var corrotto = Encoding.UTF8.GetBytes("{ \"Id\": \"NC-CR-1\", \"Uid\": ");
        File.WriteAllBytes(copia, corrotto);
        Assert.Equal(0, ConflictCopyQuarantineService.Run(state.Paths, "Op").Ritirate);
        var src = ConflictCopySource.FromInPlace(state.Paths, copia);
        Assert.NotNull(src);
        var prima = File.ReadAllBytes(path);
        var mergeChiamato = false;
        ConflictResolverUi.MergeHandler = _ => { mergeChiamato = true; return Task.FromResult(new MergeUiResult(MergeUiEsito.Annulla, null)); };
        WholeVersionRequest? req = null;
        ConflictResolverUi.WholeVersionHandler = r => { req = r; return Task.FromResult<WholeVersionChoice?>(WholeVersionChoice.TieniMia); };

        var esito = await ConflictResolutionService.RisolviCopiaAsync(state, src!);

        Assert.True(esito.Risolto);
        Assert.False(mergeChiamato);
        Assert.False(req!.Altra.Scegliibile);
        Assert.Contains("non leggibile", req.Altra.Anteprima);
        Assert.DoesNotContain("{", req.Altra.Anteprima);
        Assert.Equal(prima, File.ReadAllBytes(path));
        Assert.False(File.Exists(copia));
        var bin = Assert.Single(DomainRecoveryService.GetAll(state.Paths), r => r.VersioneScartata);
        Assert.Equal(corrotto, Convert.FromBase64String(bin.PayloadBase64));
        Assert.NotNull(nc);
    }

    [Fact]
    public async Task CopiaCorrotta_TieniLaLoro_NonPermesso_NullaCambia()
    {
        using var state = CreaStato();
        var (path, _) = SalvaNc(state);
        var copia = path.Replace(".json", " (Laptop).json", StringComparison.OrdinalIgnoreCase);
        File.WriteAllBytes(copia, Encoding.UTF8.GetBytes("non json"));
        var src = ConflictCopySource.FromInPlace(state.Paths, copia)!;
        var prima = File.ReadAllBytes(path);
        ConflictResolverUi.WholeVersionHandler = _ => Task.FromResult<WholeVersionChoice?>(WholeVersionChoice.TieniAltra);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ConflictResolutionService.RisolviCopiaAsync(state, src));

        Assert.Equal(prima, File.ReadAllBytes(path));
        Assert.True(File.Exists(copia));
        Assert.Empty(DomainRecoveryService.GetAll(state.Paths));
    }

    [Fact]
    public async Task RecordCambiatoMentreSiConfronta_RevisioneDiversa_NullaSovrascrittoEConflittoAperto()
    {
        using var state = CreaStato();
        var (path, nc) = SalvaNc(state);
        var src = Ritira(state, ScriviCopia(path, nc, n => { n.Owner = "Bruno"; n.Descrizione = "Versione del collega"; }));
        ConflictResolverUi.MergeHandler = req =>
        {
            // Mentre l'utente decide, un'altra sessione salva una nuova revisione del record (scope: la guardia ignora QUESTA copia).
            using (VisibleConflictWriteGuard.BeginResolution(new[] { src.ExemptKey }))
            {
                var concorrente = state.NonConformita.GetById("NC-CR-1")!.Clona();
                concorrente.Owner = "Concorrente";
                state.NonConformita.Save(concorrente, concorrente.RevisioneDati);
            }
            return Task.FromResult(new MergeUiResult(MergeUiEsito.Unisci, RecordMergeChoices.Tutti(RecordFieldChoice.Loro, req.Plan)));
        };

        await Assert.ThrowsAsync<ConflittoRevisioneException>(() => ConflictResolutionService.RisolviCopiaAsync(state, src));

        Assert.Equal("Concorrente", state.NonConformita.GetById("NC-CR-1")!.Owner); // la modifica concorrente non e' stata sovrascritta
        Assert.Single(ConflictCopyQuarantineService.List(state.Paths.Root));         // il conflitto resta aperto
        Assert.Contains(DomainRecoveryService.GetAll(state.Paths), r => r.VersioneScartata); // al piu' copie in piu' nel cestino
    }

    [Fact]
    public async Task IdentificativiDiversi_LaltraVersionePuoEssereSoloScartata()
    {
        using var state = CreaStato();
        var (path, nc) = SalvaNc(state);
        var altra = EntityStateFingerprint.Clone(nc);
        altra.Id = "NC-ALTRO-ID";
        var copia = path.Replace(".json", " (Laptop).json", StringComparison.OrdinalIgnoreCase);
        JsonIo.WriteAtomic(copia, altra);
        var src = Ritira(state, copia);
        var mergeChiamato = false;
        ConflictResolverUi.MergeHandler = _ => { mergeChiamato = true; return Task.FromResult(new MergeUiResult(MergeUiEsito.Annulla, null)); };
        WholeVersionRequest? req = null;
        ConflictResolverUi.WholeVersionHandler = r => { req = r; return Task.FromResult<WholeVersionChoice?>(WholeVersionChoice.TieniMia); };

        var esito = await ConflictResolutionService.RisolviCopiaAsync(state, src);

        Assert.True(esito.Risolto);
        Assert.False(mergeChiamato);
        Assert.False(req!.Altra.Scegliibile);
        Assert.Contains("identificativi diversi", req.Motivo);
    }

    [Fact]
    public async Task VersioneIntera_ValoreNonValido_MotivoChiaroEDifferenzeEvidenziate()
    {
        using var state = CreaStato();
        var (path, nc) = SalvaNc(state);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        json["Priorita"] = "High";
        json["Owner"] = "Bruno";
        var copia = path.Replace(".json", " (Laptop).json", StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(copia, json.ToJsonString());
        var src = Ritira(state, copia);
        WholeVersionRequest? req = null;
        ConflictResolverUi.WholeVersionHandler = r => { req = r; return Task.FromResult<WholeVersionChoice?>(WholeVersionChoice.TieniMia); };

        var esito = await ConflictResolutionService.RisolviCopiaAsync(state, src);

        Assert.True(esito.Risolto);
        Assert.False(req!.Altra.Scegliibile);
        Assert.Contains("Priorità interna", req.Altra.Problema);
        Assert.Contains("High", req.Altra.Problema);
        Assert.Contains("Priorità interna", req.Motivo);
        Assert.Contains(req.Altra.Campi!, c => c.Etichetta == "Owner" && c.Diverso);
        Assert.Contains(req.Mia.Campi!, c => c.Etichetta == "Owner" && c.Diverso);
        Assert.Contains(req.Mia.Campi!, c => c.Etichetta == "Descrizione" && !c.Diverso);
        Assert.NotNull(nc);
    }

    [Fact]
    public async Task SenzaUi_NessunaRisoluzioneImplicita()
    {
        using var state = CreaStato();
        var (path, nc) = SalvaNc(state);
        var src = Ritira(state, ScriviCopia(path, nc, n => n.Owner = "Bruno"));
        var prima = File.ReadAllBytes(path);

        var esito = await ConflictResolutionService.RisolviCopiaAsync(state, src);

        Assert.False(esito.Risolto);
        Assert.Equal(prima, File.ReadAllBytes(path));
        Assert.Single(ConflictCopyQuarantineService.List(state.Paths.Root));
    }

    [Fact]
    public async Task NonMaster_NonPuoRisolvere()
    {
        using var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var src = new ConflictCopySource { Category = DomainRecoveryCategory.NonConformita, Uid = Guid.NewGuid(), OtherBytes = new byte[] { 1 } };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ConflictResolutionService.RisolviCopiaAsync(state, src));
    }
}

[CollectionDefinition("ConflictResolverUi", DisableParallelization = true)]
public sealed class ConflictResolverUiCollection { }
