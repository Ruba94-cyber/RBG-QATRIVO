using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Salvataggio atomico di modifiche su piu' cataloghi master (associazioni dal veicolo: Componente e Funzionalita).
/// Su Phase D le mutation di tutti i cataloghi finiscono in UN solo batch del journal: una riga NDJSON, quindi o ci
/// sono tutte o nessuna. Senza Phase D (workspace legacy) ripiega su una transazione di workflow sui file JSON, come
/// Unisci/Rinomina/Dividi. Le verifiche di concorrenza (fingerprint di catalogo, testa remota, baseline) sono quelle
/// di SavePatch: un conflitto lancia MasterDataConflictException e non scrive nulla. Sul ramo legacy l'impronta di
/// catalogo e allowSilentRemoteMerge vengono solo passati al Save, che non li usa: la protezione e' il confronto con la
/// fotografia di apertura (Attese), fatto sotto il lock di commit del file.
/// </summary>
public static class MasterDataSalvataggioAtomico
{
    /// <summary>Stato di un catalogo: <paramref name="Desiderate"/> e' cio' che si vuole scrivere, <paramref name="Attese"/> la
    /// fotografia letta all'apertura (baseline) e <paramref name="ImpronteCatalogo"/> l'impronta catturata con quella lettura
    /// (null se assente, come per un Save legacy).</summary>
    public sealed record Modifiche<T>(IReadOnlyList<T> Desiderate, IReadOnlyList<T> Attese, string? ImpronteCatalogo) where T : MasterEntity;

    public static void Salva(DataPaths paths, MasterDataStore master, Modifiche<Componente> componente, Modifiche<Funzionalita> funzionalita, bool allowSilentRemoteMerge)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(componente);
        ArgumentNullException.ThrowIfNull(funzionalita);
        var repoEcu = master.Componente;
        var repoFunzionalita = master.Funzionalita;

        var pianoEcu = repoEcu.PianificaPatchPhaseD(componente.Desiderate, componente.Attese, allowSilentRemoteMerge, componente.ImpronteCatalogo);
        var pianoFunzionalita = repoFunzionalita.PianificaPatchPhaseD(funzionalita.Desiderate, funzionalita.Attese, allowSilentRemoteMerge, funzionalita.ImpronteCatalogo);

        // Stessa radice e stesso file-ready per entrambi: o entrambi sono su Phase D o nessuno. Un esito misto non deve
        // mai scrivere una parte sola, quindi si rifiuta.
        if ((pianoEcu == null) != (pianoFunzionalita == null))
            throw new InvalidOperationException("Componente e Funzionalità non sono sullo stesso stato di archiviazione Phase D: salvataggio annullato, nessuna modifica scritta.");

        if (pianoEcu != null && pianoFunzionalita != null)
        {
            var tutte = pianoEcu.Mutations.Concat(pianoFunzionalita.Mutations).ToList();
            if (tutte.Count == 0) return;
            // Un solo batch: stesso formato e stesso contatore di device dei Save singoli (PhaseDMasterBatchWriter).
            PhaseDMasterBatchWriter.Pubblica(pianoEcu.Engine.Percorsi, pianoEcu.Engine.DirectoriaLocaleBase, tutte);
            pianoEcu.Engine.VerificaPubblicate(pianoEcu.Mutations);
            pianoFunzionalita.Engine.VerificaPubblicate(pianoFunzionalita.Mutations);
            DiagnosticTraceService.Registra("MasterData.SalvaAtomico", $"componente={pianoEcu.Mutations.Count} funzionalita={pianoFunzionalita.Mutations.Count} batch=1");
            return;
        }

        // Workspace non ancora su Phase D: i due file JSON stanno nella stessa transazione (rollback se il secondo fallisce).
        using var tx = WorkflowTransactionService.Begin(paths, "associazioni-veicolo:componente+funzionalita");
        repoEcu.Save(componente.Desiderate.ToList(), componente.Attese, allowSilentRemoteMerge, componente.ImpronteCatalogo);
        repoFunzionalita.Save(funzionalita.Desiderate.ToList(), funzionalita.Attese, allowSilentRemoteMerge, funzionalita.ImpronteCatalogo);
        tx.Commit();
    }
}
