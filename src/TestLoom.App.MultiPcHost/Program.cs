using System.Text.Json;
using System.Text.Json.Nodes;
using TestLoom.App.Models;
using TestLoom.App.Services;

// FASE 4 (multi-PC 6.4.2): host headless che simula UN PC reale come processo OS separato. Il chiamante
// (MultiPcSyncTests) avvia N istanze di questo eseguibile, una per "PC", ognuna con il proprio --local (radice
// %LOCALAPPDATA% simulata: identità device, indici SQLite locali, journal — tutto isolato) e la STESSA
// --shared (cartella dati condivisa, come una copia locale OneDrive/SharePoint). Protocollo: una riga JSON in
// stdin -> una riga JSON in stdout, "READY" iniziale dopo l'inizializzazione.

string? sharedRoot = null;
string? localRoot = null;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--shared") sharedRoot = args[i + 1];
    if (args[i] == "--local") localRoot = args[i + 1];
}
if (string.IsNullOrWhiteSpace(sharedRoot) || string.IsNullOrWhiteSpace(localRoot))
{
    Console.Error.WriteLine("Uso: TestLoom.App.MultiPcHost --shared <cartella condivisa> --local <cartella LOCALAPPDATA simulata>");
    return 2;
}

Directory.CreateDirectory(localRoot);
// Deve essere impostato PRIMA di qualunque chiamata che tocchi DeviceIdentityService/indici locali: questo
// singolo processo avrà quindi il proprio device-id stabile e i propri indici SQLite, come un vero secondo PC.
WorkspaceLocalPaths.LocalApplicationDataOverrideForTests = localRoot;
// Stesso per crash.log/trace.log (e config): sotto la radice locale di QUESTO PC simulato, mai in %APPDATA% reale.
TestLoomDataRoot.ImpostaRadiceProcesso(localRoot);

var configPath = Path.Combine(localRoot, "config.json");
var state = new AppState(new AppConfigService(configPath), runBackgroundMaintenance: false);
state.InizializzaCartella(sharedRoot);

Console.WriteLine(JsonSerializer.Serialize(new
{
    ok = true,
    evento = "ready",
    deviceId = DeviceIdentityService.GetOrCreate(),
    cartellaValida = state.CartellaValida
}));

string? riga;
while ((riga = Console.ReadLine()) != null)
{
    if (string.IsNullOrWhiteSpace(riga)) continue;
    try
    {
        var comando = JsonNode.Parse(riga)!.AsObject();
        var cmd = comando["cmd"]?.GetValue<string>() ?? "";
        var risposta = Esegui(cmd, comando);
        Console.WriteLine(risposta.ToJsonString());
    }
    catch (Exception ex)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { ok = false, errore = ex.GetType().Name + ": " + ex.Message }));
    }
}
return 0;

JsonObject Esegui(string cmd, JsonObject c)
{
    switch (cmd)
    {
        case "refresh":
        {
            state.RileggiDaDisco();
            return Ok();
        }
        case "createTest":
        {
            var tc = new TestCase
            {
                Id = Str(c, "id"),
                Componente = Str(c, "componente", "ECM"),
                Veicolo = Str(c, "veicolo", "Daily"),
                Veicoli = [Str(c, "veicolo", "Daily")],
                DescrizioneBreve = Str(c, "descrizione", ""),
                Descrizione = Str(c, "descrizione", "")
            };
            state.TestCases.Save(tc, isNew: true);
            return Ok(new JsonObject { ["uid"] = tc.Uid.ToString(), ["revisione"] = tc.RevisioneDati });
        }
        case "getTest":
        {
            var tc = state.TestCases.GetById(Str(c, "id"));
            if (tc == null) return Ok(new JsonObject { ["trovato"] = false });
            return Ok(new JsonObject
            {
                ["trovato"] = true,
                ["uid"] = tc.Uid.ToString(),
                ["descrizione"] = tc.DescrizioneBreve,
                ["descrizioneEstesa"] = tc.Descrizione,
                ["assegnatario"] = tc.Assegnatario ?? "",
                ["revisione"] = tc.RevisioneDati,
                ["eliminato"] = tc.Eliminato
            });
        }
        case "editTest":
        {
            // Modifica solo i campi presenti nel comando (descrizione breve, descrizione estesa, assegnatario): due
            // PC possono così cambiare campi DIVERSI dello stesso Test, come due utenti reali in editor. Con la sola
            // "descrizione" il comportamento resta quello storico (breve ed estesa allineate).
            var tc = state.TestCases.GetById(Str(c, "id"))
                ?? throw new InvalidOperationException($"Test '{Str(c, "id")}' non trovato su questo PC.");
            if (c.ContainsKey("descrizione"))
            {
                tc.DescrizioneBreve = Str(c, "descrizione");
                if (!c.ContainsKey("descrizioneEstesa")) tc.Descrizione = tc.DescrizioneBreve;
            }
            if (c.ContainsKey("descrizioneEstesa")) tc.Descrizione = Str(c, "descrizioneEstesa");
            if (c.ContainsKey("assegnatario")) tc.Assegnatario = Str(c, "assegnatario");
            state.TestCases.Save(tc, isNew: false, revisioneAttesa: tc.RevisioneDati);
            return Ok(new JsonObject { ["revisione"] = tc.RevisioneDati });
        }
        case "getTestAnche":
        {
            // Come getTest ma include i Test nel cestino (Eliminato = true).
            var tc = state.TestCases.GetAll(includeEliminati: true).FirstOrDefault(t => t.Id == Str(c, "id"));
            if (tc == null) return Ok(new JsonObject { ["trovato"] = false });
            return Ok(new JsonObject { ["trovato"] = true, ["eliminato"] = tc.Eliminato, ["componente"] = tc.Componente.ToString(), ["descrizione"] = tc.DescrizioneBreve });
        }
        case "restoreTest":
        {
            var tc = state.TestCases.GetAll(includeEliminati: true).FirstOrDefault(t => t.Id == Str(c, "id"))
                ?? throw new InvalidOperationException($"Test '{Str(c, "id")}' non trovato su questo PC.");
            state.TestCases.Restore(tc, Str(c, "operatore", "HOST"));
            return Ok();
        }
        case "purgeTest":
        {
            var tc = state.TestCases.GetAll(includeEliminati: true).FirstOrDefault(t => t.Id == Str(c, "id"))
                ?? throw new InvalidOperationException($"Test '{Str(c, "id")}' non trovato su questo PC.");
            state.TestCases.EliminaDefinitivamente(tc);
            return Ok();
        }
        case "moveTest":
        {
            // Cambio componente: il repository sposta il file nella cartella della nuova componente (move-test).
            var tc = state.TestCases.GetById(Str(c, "id"))
                ?? throw new InvalidOperationException($"Test '{Str(c, "id")}' non trovato su questo PC.");
            tc.Componente = Str(c, "componente");
            state.TestCases.Save(tc, isNew: false, revisioneAttesa: tc.RevisioneDati);
            return Ok(new JsonObject { ["revisione"] = tc.RevisioneDati });
        }
        case "lockTest":
        {
            var tc = state.TestCases.GetById(Str(c, "id"))
                ?? throw new InvalidOperationException($"Test '{Str(c, "id")}' non trovato su questo PC.");
            var esito = state.Locks.TryAcquire(state.TestCases.PercorsoPer(tc), Str(c, "operatore", "HOST"));
            return Ok(new JsonObject { ["esito"] = esito.ToString() });
        }
        case "releaseLock":
        {
            var tc = state.TestCases.GetById(Str(c, "id"))
                ?? throw new InvalidOperationException($"Test '{Str(c, "id")}' non trovato su questo PC.");
            state.Locks.Release(state.TestCases.PercorsoPer(tc));
            return Ok();
        }
        case "avvisoAltraPostazione":
        {
            // Stessa regola dell'editor (TestCaseEditorViewModel.RilevaLockAltraPostazione): lock attivo non
            // scaduto, e mai un avviso per il proprio lock (stesso operatore E stessa postazione).
            var tc = state.TestCases.GetById(Str(c, "id"))
                ?? throw new InvalidOperationException($"Test '{Str(c, "id")}' non trovato su questo PC.");
            var attivo = state.Locks.GetActiveLock(state.TestCases.PercorsoPer(tc));
            var proprio = attivo != null &&
                attivo.Operatore.Equals(Str(c, "operatore", "HOST"), StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(attivo.DeviceId) &&
                attivo.DeviceId.Equals(DeviceIdentityService.GetOrCreate(), StringComparison.OrdinalIgnoreCase);
            var avviso = attivo != null && !proprio;
            return Ok(new JsonObject { ["avviso"] = avviso, ["operatore"] = avviso ? attivo!.Operatore : "" });
        }
        case "addEcu":
        {
            var componente = state.Master.Componente.Load();
            componente.Add(new Componente { Nome = Str(c, "nome") });
            state.Master.Componente.Save(componente);
            return Ok();
        }
        case "editEcu":
        {
            // Modifica una proprietà NON chiave (Archiviata) del componente con quel nome.
            var componente = state.Master.Componente.Load();
            var voce = componente.First(e => e.Nome == Str(c, "nome"));
            voce.Archiviata = Str(c, "archiviata") == "true";
            state.Master.Componente.Save(componente);
            return Ok();
        }
        case "listEcu":
        {
            var arr = new JsonArray();
            foreach (var e in state.Master.Componente.Load()) arr.Add(JsonValue.Create(e.Nome));
            return Ok(new JsonObject { ["componente"] = arr });
        }
        case "createCR":
        {
            var cr = new ChangeRequest { Id = Str(c, "id"), Titolo = Str(c, "titolo") };
            state.ChangeRequests.Save(cr);
            return Ok();
        }
        case "editCR":
        {
            var cr = state.ChangeRequests.GetById(Str(c, "id"))
                ?? throw new InvalidOperationException($"CR '{Str(c, "id")}' non trovata su questo PC.");
            cr.Titolo = Str(c, "titolo", cr.Titolo);
            state.ChangeRequests.Save(cr, cr.RevisioneDati);
            return Ok();
        }
        case "getCR":
        {
            var cr = state.ChangeRequests.GetById(Str(c, "id"));
            return Ok(new JsonObject { ["trovato"] = cr != null, ["titolo"] = cr?.Titolo ?? "" });
        }
        case "createSeg":
        {
            var s = new SegnalazioneDaClassificare { Uid = Guid.Parse(Str(c, "uid")), TestId = Str(c, "testId") };
            state.Segnalazioni.Save(s);
            return Ok();
        }
        case "editSeg":
        {
            var s = state.Segnalazioni.Get(Guid.Parse(Str(c, "uid")))
                ?? throw new InvalidOperationException("Segnalazione non trovata su questo PC.");
            s.MotivazioneEsclusione = Str(c, "motivazione");
            state.Segnalazioni.Save(s, s.RevisioneDati);
            return Ok();
        }
        case "getSeg":
        {
            var s = state.Segnalazioni.Get(Guid.Parse(Str(c, "uid")));
            return Ok(new JsonObject { ["trovato"] = s != null, ["motivazione"] = s?.MotivazioneEsclusione ?? "" });
        }
        case "listForks":
        {
            var arr = new JsonArray();
            foreach (var f in state.TestCases.JournalForks)
                arr.Add(new JsonObject { ["uid"] = f.EntityUid.ToString(), ["id"] = f.EntityId, ["rami"] = f.Branches.Count });
            return Ok(new JsonObject { ["forks"] = arr });
        }
        case "deleteTest":
        {
            var tc = state.TestCases.GetById(Str(c, "id"))
                ?? throw new InvalidOperationException($"Test '{Str(c, "id")}' non trovato su questo PC.");
            state.TestCases.SoftDelete(tc, Str(c, "operatore", "HOST"));
            return Ok();
        }
        case "createNC":
        {
            var nc = new NonConformita
            {
                Id = Str(c, "id"),
                TestCaseId = Str(c, "testId"),
                Descrizione = Str(c, "descrizione", "")
            };
            state.NonConformita.Save(nc);
            return Ok(new JsonObject { ["uid"] = nc.Uid.ToString(), ["revisione"] = nc.RevisioneDati });
        }
        case "getNC":
        {
            var nc = state.NonConformita.GetById(Str(c, "id"));
            if (nc == null) return Ok(new JsonObject { ["trovato"] = false });
            return Ok(new JsonObject
            {
                ["trovato"] = true,
                ["testCaseId"] = nc.TestCaseId,
                ["descrizione"] = nc.Descrizione,
                ["revisione"] = nc.RevisioneDati
            });
        }
        case "listTests":
        {
            var arr = new JsonArray();
            foreach (var tc in state.TestCases.GetAll())
                arr.Add(new JsonObject { ["id"] = tc.Id, ["descrizione"] = tc.DescrizioneBreve, ["revisione"] = tc.RevisioneDati });
            return Ok(new JsonObject { ["tests"] = arr });
        }
        case "listProblemi":
        {
            var problemi = new JsonArray();
            foreach (var p in state.TuttiIFileConProblemi())
                problemi.Add(new JsonObject { ["percorso"] = p.Percorso, ["errore"] = p.Errore });
            var conflitti = new JsonArray();
            foreach (var cc in state.TutteLeCopieDiConflitto())
                conflitti.Add(new JsonObject { ["percorso"] = cc.Percorso, ["errore"] = cc.Errore });
            var duplicati = new JsonArray();
            foreach (var d in state.TestCases.DuplicatiId)
                duplicati.Add(new JsonObject { ["id"] = d.Id, ["percorsi"] = new JsonArray(d.Percorsi.Select(p => JsonValue.Create(p)).ToArray()) });
            return Ok(new JsonObject { ["fileConProblemi"] = problemi, ["copieDiConflitto"] = conflitti, ["duplicatiId"] = duplicati });
        }
        case "ping":
            return Ok();
        default:
            return new JsonObject { ["ok"] = false, ["errore"] = $"Comando sconosciuto: {cmd}" };
    }
}

JsonObject Ok(JsonObject? extra = null)
{
    var o = new JsonObject { ["ok"] = true };
    if (extra != null)
        foreach (var kv in extra.ToList())
        {
            extra.Remove(kv.Key);
            o[kv.Key] = kv.Value;
        }
    return o;
}

string Str(JsonObject c, string campo, string fallback = "") => c[campo]?.GetValue<string>() ?? fallback;
