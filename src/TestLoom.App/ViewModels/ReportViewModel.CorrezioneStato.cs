using CommunityToolkit.Mvvm.ComponentModel;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Riga del pannello di correzione del Master (6.6.1, fase 2): una combinazione sottostante a una cella della matrice.
/// Fonte = esito eseguito più recente su un altro veicolo della stessa componente (propagazione del "!"), se esiste.</summary>
public sealed record RigaCorrezioneStato(string Funzionalita, string Componente, string Veicolo, StatoCombinazioneMatrice Stato,
    bool ManualeVincente, bool ManualeSuperato, bool Propagabile, FonteStatoPropagato? Fonte);

/// <summary>Scelta del Master per una riga del pannello: valore manuale oppure propagazione dalla fonte indicata.</summary>
public sealed record CorrezioneRigaStato(string Funzionalita, string Componente, string Veicolo, Esito? EsitoManuale, bool Propaga, FonteStatoPropagato? Fonte);

/// <summary>Indice dei migliori candidati di propagazione per le combinazioni della cella aggregata. Costruito con una
/// sola enumerazione dell'archivio prima di comporre le righe del pannello.</summary>
internal sealed class IndiceFontiStatoPropagato
{
    private readonly Dictionary<ChiaveFonte, CandidatoFonte> _migliori = new(ChiaveFonteComparer.Instance);

    private readonly record struct ChiaveGruppo(string Funzionalita, string Componente);
    private readonly record struct ChiaveFonte(string Funzionalita, string Componente, string Veicolo);
    private readonly record struct OrigineVeicolo(string? Principale, string? Alternativa);

    private sealed class ChiaveGruppoComparer : IEqualityComparer<ChiaveGruppo>
    {
        public static readonly ChiaveGruppoComparer Instance = new();

        public bool Equals(ChiaveGruppo x, ChiaveGruppo y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Funzionalita, y.Funzionalita) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Componente, y.Componente);

        public int GetHashCode(ChiaveGruppo obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Funzionalita),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Componente));
    }

    private sealed class ChiaveFonteComparer : IEqualityComparer<ChiaveFonte>
    {
        public static readonly ChiaveFonteComparer Instance = new();

        public bool Equals(ChiaveFonte x, ChiaveFonte y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Funzionalita, y.Funzionalita) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Componente, y.Componente) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Veicolo, y.Veicolo);

        public int GetHashCode(ChiaveFonte obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Funzionalita),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Componente),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Veicolo));
    }

    private sealed class GruppoDestinazioni
    {
        public required string Funzionalita { get; init; }
        public required string Componente { get; init; }
        public required HashSet<string> VeicoliEcu { get; init; }
        public HashSet<string> VeicoliDestinazione { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, RiepilogoOrigine> CandidatiPerOrigine { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class RiepilogoOrigine
    {
        public CandidatoFonte? MiglioreEsterno { get; set; }
        public CandidatoFonte? MiglioreConAlternativa { get; set; }
    }

    private sealed record CandidatoFonte(
        DateTime Data, string TestIdOrdine, int RevisioneIndice, int TentativoIndice,
        string VeicoloOrigine, FonteStatoPropagato FonteEsterno, FonteStatoPropagato? FontePropria,
        long Sequenza);

    internal int CandidatiConsiderati { get; private set; }
    internal int ConfrontiCandidati { get; private set; }
    internal int DestinazioniRisolte { get; private set; }

    internal static IndiceFontiStatoPropagato Crea(
        IEnumerable<TestCase> testCases,
        IReadOnlyList<Componente> catalogoEcu,
        IReadOnlyList<StatoFunzionalitaEcuVeicolo> manuali,
        IEnumerable<CombinazioneCellaMatrice> destinazioni)
    {
        var gruppi = new Dictionary<ChiaveGruppo, GruppoDestinazioni>(ChiaveGruppoComparer.Instance);
        var ecuPerNome = new Dictionary<string, Componente>(StringComparer.OrdinalIgnoreCase);
        foreach (var definizione in catalogoEcu)
            ecuPerNome.TryAdd(definizione.Nome, definizione);

        foreach (var destinazione in destinazioni)
        {
            if (string.IsNullOrWhiteSpace(destinazione.Funzionalita) ||
                !ecuPerNome.TryGetValue(destinazione.Componente, out var definizioneEcu))
                continue;

            var chiave = new ChiaveGruppo(destinazione.Funzionalita, destinazione.Componente);
            if (!gruppi.TryGetValue(chiave, out var gruppo))
            {
                gruppo = new GruppoDestinazioni
                {
                    Funzionalita = destinazione.Funzionalita,
                    Componente = destinazione.Componente,
                    VeicoliEcu = new HashSet<string>(definizioneEcu.VeicoliAssociati, StringComparer.OrdinalIgnoreCase),
                };
                gruppi.Add(chiave, gruppo);
            }
            gruppo.VeicoliDestinazione.Add(destinazione.Veicolo);
        }

        var indice = new IndiceFontiStatoPropagato();
        long sequenza = 0;
        foreach (var test in testCases)
        {
            var veicoliTest = test.VeicoliEffettivi;
            var originiPerGruppo = new Dictionary<ChiaveGruppo, OrigineVeicolo>(ChiaveGruppoComparer.Instance);
            for (var revisioneIndice = 0; revisioneIndice < test.Esecuzioni.Count; revisioneIndice++)
            {
                var revisione = test.Esecuzioni[revisioneIndice];
                var condizioni = revisione.CondizioniEffettive;
                for (var tentativoIndice = 0; tentativoIndice < condizioni.Count; tentativoIndice++)
                {
                    var condizione = condizioni[tentativoIndice];
                    if (!condizione.Esito.EEseguito() || string.IsNullOrWhiteSpace(condizione.Funzionalita))
                        continue;

                    foreach (var componente in condizione.Componente)
                    {
                        var chiaveGruppo = new ChiaveGruppo(condizione.Funzionalita, componente);
                        if (!gruppi.TryGetValue(chiaveGruppo, out var gruppo))
                            continue;

                        if (!originiPerGruppo.TryGetValue(chiaveGruppo, out var origine))
                        {
                            var principale = veicoliTest.FirstOrDefault(gruppo.VeicoliEcu.Contains);
                            var alternativa = principale == null
                                ? null
                                : veicoliTest.FirstOrDefault(v =>
                                    !StringComparer.OrdinalIgnoreCase.Equals(v, principale) && gruppo.VeicoliEcu.Contains(v));
                            origine = new OrigineVeicolo(principale, alternativa);
                            originiPerGruppo.Add(chiaveGruppo, origine);
                        }

                        if (origine.Principale == null)
                            continue;

                        var fonteEsterno = new FonteStatoPropagato(origine.Principale, test.Id, condizione.Esito);
                        var fontePropria = origine.Alternativa == null
                            ? null
                            : new FonteStatoPropagato(origine.Alternativa, test.Id, condizione.Esito);
                        indice.Registra(gruppo, new CandidatoFonte(
                            revisione.Data, test.Id, revisioneIndice, tentativoIndice,
                            origine.Principale, fonteEsterno, fontePropria, sequenza++));
                    }
                }
            }
        }

        foreach (var manuale in manuali)
        {
            if (!manuale.Esito.EEseguito() ||
                !gruppi.TryGetValue(new ChiaveGruppo(manuale.Funzionalita, manuale.Componente), out var gruppo) ||
                !gruppo.VeicoliEcu.Contains(manuale.Veicolo))
                continue;

            var idOrdine = ReportMatriceService.ManualePrefisso + manuale.Uid.ToString("N");
            var fonte = new FonteStatoPropagato(manuale.Veicolo, "manuale", manuale.Esito);
            indice.Registra(gruppo, new CandidatoFonte(
                manuale.DataDecisione, idOrdine, 0, 0, manuale.Veicolo, fonte, null, sequenza++));
        }

        foreach (var gruppo in gruppi.Values)
        {
            var (migliore, secondaMigliore) = indice.MiglioriOrigini(gruppo);
            foreach (var veicoloDestinazione in gruppo.VeicoliDestinazione)
            {
                var esterna = migliore != null &&
                    !StringComparer.OrdinalIgnoreCase.Equals(migliore.VeicoloOrigine, veicoloDestinazione)
                        ? migliore
                        : secondaMigliore;
                if (esterna != null &&
                    StringComparer.OrdinalIgnoreCase.Equals(esterna.VeicoloOrigine, veicoloDestinazione))
                    esterna = null;

                gruppo.CandidatiPerOrigine.TryGetValue(veicoloDestinazione, out var riepilogoProprio);
                var propria = riepilogoProprio?.MiglioreConAlternativa;
                var vincente = indice.Migliore(esterna, propria);
                if (vincente != null)
                {
                    if (ReferenceEquals(vincente, propria))
                        vincente = vincente with { FonteEsterno = vincente.FontePropria! };
                    indice._migliori.Add(
                        new ChiaveFonte(gruppo.Funzionalita, gruppo.Componente, veicoloDestinazione), vincente);
                }
                indice.DestinazioniRisolte++;
            }
        }

        return indice;
    }

    internal FonteStatoPropagato? FonteDi(string funzionalita, string componente, string veicolo)
    {
        var chiave = new ChiaveFonte(funzionalita, componente, veicolo);
        return _migliori.TryGetValue(chiave, out var candidato) ? candidato.FonteEsterno : null;
    }

    private void Registra(GruppoDestinazioni gruppo, CandidatoFonte candidato)
    {
        CandidatiConsiderati++;
        if (!gruppo.CandidatiPerOrigine.TryGetValue(candidato.VeicoloOrigine, out var riepilogo))
        {
            riepilogo = new RiepilogoOrigine();
            gruppo.CandidatiPerOrigine.Add(candidato.VeicoloOrigine, riepilogo);
        }

        riepilogo.MiglioreEsterno = Migliore(riepilogo.MiglioreEsterno, candidato);
        if (candidato.FontePropria != null)
            riepilogo.MiglioreConAlternativa = Migliore(riepilogo.MiglioreConAlternativa, candidato);
    }

    private (CandidatoFonte? Migliore, CandidatoFonte? SecondaMigliore) MiglioriOrigini(GruppoDestinazioni gruppo)
    {
        CandidatoFonte? migliore = null;
        CandidatoFonte? seconda = null;
        foreach (var riepilogo in gruppo.CandidatiPerOrigine.Values)
        {
            var candidato = riepilogo.MiglioreEsterno;
            if (candidato == null) continue;

            if (Migliore(migliore, candidato) == candidato)
            {
                seconda = migliore;
                migliore = candidato;
            }
            else
            {
                seconda = Migliore(seconda, candidato);
            }
        }

        return (migliore, seconda);
    }

    private CandidatoFonte? Migliore(CandidatoFonte? primo, CandidatoFonte? secondo)
    {
        if (primo == null) return secondo;
        if (secondo == null) return primo;

        ConfrontiCandidati++;
        if (ReportMatriceService.CombinazioneAccumulatore.Successivo(
                secondo.Data, secondo.TestIdOrdine, secondo.RevisioneIndice, secondo.TentativoIndice,
                primo.Data, primo.TestIdOrdine, primo.RevisioneIndice, primo.TentativoIndice))
            return secondo;
        if (ReportMatriceService.CombinazioneAccumulatore.Successivo(
                primo.Data, primo.TestIdOrdine, primo.RevisioneIndice, primo.TentativoIndice,
                secondo.Data, secondo.TestIdOrdine, secondo.RevisioneIndice, secondo.TentativoIndice))
            return primo;
        return primo.Sequenza <= secondo.Sequenza ? primo : secondo;
    }
}

/// <summary>Correzione manuale dello stato nel Report (6.6.1, fase 2). Solo Master: il controllo è anche nel servizio
/// (StatoFunzionaleManualeService). Il pannello si apre dal menu contestuale della cella (solo Master).</summary>
public partial class ReportViewModel
{
    /// <summary>Filtro rapido del Master: mostra solo le righe con almeno una cella "!" da decidere.</summary>
    [ObservableProperty] private bool soloCelleConDaDecidere;

    partial void OnSoloCelleConDaDecidereChanged(bool value)
    {
        ReimpostaPosizioneRigheMatrice();
        ApplicaMatrice();
    }

    /// <summary>Il menu di correzione è attivo solo per il Master (Operatore e Viewer vedono valore e nota nel tooltip).</summary>
    public bool PuoCorreggereStato => _state.EMaster;

    /// <summary>Combinazioni sottostanti a una cella (anche aggregata), con lo stato e la fonte di propagazione del "!".</summary>
    /// <summary>Combinazioni sottostanti a una cella, con le fonti calcolate da un solo indice per apertura pannello.</summary>
    public IReadOnlyList<RigaCorrezioneStato> RigheCorrezioneCella(CellaMatriceReport cella)
    {
        if (_matricePreparata == null) return Array.Empty<RigaCorrezioneStato>();

        var combinazioni = _matricePreparata.CombinazioniDi(cella.Riga, cella.Colonna)
            .OrderBy(x => x.Funzionalita, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Componente, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Veicolo, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var manuali = _state.Master.StatiFunzionalitaEcuVeicolo.Load();
        var indiceFonti = IndiceFontiStatoPropagato.Crea(
            _state.TestCases.GetAll(), _state.Master.Componente.Load(), manuali, combinazioni);

        return combinazioni.Select(c => new RigaCorrezioneStato(
            c.Funzionalita, c.Componente, c.Veicolo, c.Stato, c.ManualeVincente, c.ManualeSuperato, c.Propagabile,
            indiceFonti.FonteDi(c.Funzionalita, c.Componente, c.Veicolo))).ToArray();
    }

    /// <summary>Applica le scelte del pannello: valori manuali (un'unica decisione per funzionalità ed componente) e propagazioni.
    /// Nota obbligatoria. Errori (permessi, nota, esito non eseguito) tornano in <paramref name="errore"/>.</summary>
    public bool SalvaCorrezioneStato(IEnumerable<CorrezioneRigaStato> scelte, string nota, out string? errore)
    {
        var lista = scelte.ToList();
        var operatore = _state.Config.OperatoreAttivo;
        var repo = _state.Master.StatiFunzionalitaEcuVeicolo;
        errore = null;
        foreach (var gruppo in lista.Where(s => s.EsitoManuale.HasValue).GroupBy(s => (s.Funzionalita, s.Componente, Esito: s.EsitoManuale!.Value)))
        {
            if (!StatoFunzionaleManualeService.ImpostaManuale(repo, gruppo.Key.Funzionalita, gruppo.Key.Componente,
                    gruppo.Select(s => s.Veicolo), gruppo.Key.Esito, nota, operatore, _state.EMaster, out errore))
                return false;
        }
        foreach (var gruppo in lista.Where(s => s.Propaga && s.Fonte != null).GroupBy(s => (s.Funzionalita, s.Componente, Fonte: s.Fonte!)))
        {
            if (!StatoFunzionaleManualeService.Propaga(repo, gruppo.Key.Funzionalita, gruppo.Key.Componente,
                    gruppo.Select(s => (s.Veicolo, gruppo.Key.Fonte)), nota, operatore, _state.EMaster, out errore))
                return false;
        }
        GeneraMatrice();
        return true;
    }

    /// <summary>Rimuove il valore manuale delle combinazioni scelte: tornano a valere i Test (o il "!" se propagabili).</summary>
    public bool RimuoviInserimentoManuale(IEnumerable<RigaCorrezioneStato> righe, out string? errore)
    {
        errore = null;
        foreach (var r in righe.Where(x => x.ManualeVincente || x.ManualeSuperato))
        {
            if (!StatoFunzionaleManualeService.RimuoviManuale(_state.Master.StatiFunzionalitaEcuVeicolo, r.Funzionalita, r.Componente, r.Veicolo, _state.EMaster, out errore))
                return false;
        }
        GeneraMatrice();
        return true;
    }
}
