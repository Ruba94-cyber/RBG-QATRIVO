using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Associazioni viste dal veicolo (Veicoli > "Componente e funzionalità"): quali componente sono montate sul veicolo
/// (<see cref="Componente.VeicoliAssociati"/>) e, per ciascuna componente, quali funzionalità valgono su quel veicolo
/// (<see cref="Funzionalita.EcuAssociate"/> e <see cref="Funzionalita.VeicoliPerEcu"/>).
///
/// Logica pura: non legge né scrive file, non acquisisce lock e non tocca la UI. Le regole di applicabilità restano nel
/// modello (<see cref="Funzionalita.ValeSuEcu"/>, <see cref="Funzionalita.ValeSu"/>, <see cref="Funzionalita.VeicoliRistretti"/>);
/// qui si decide solo come cambiare le restrizioni senza alterare il comportamento degli altri veicoli.
///
/// Semantica delle restrizioni per componente: senza voce, la funzionalità vale su tutti i veicoli del componente; con una voce, solo su
/// quelli elencati; una voce con elenco vuoto significa nessun veicolo.
///
/// Scelta sulla sincronizzazione (lettura intersecata, nessuna scrittura cross-catalogo): una voce vale solo sui veicoli ancora
/// montati sul componente (<see cref="Componente.VeicoliAssociati"/>). La pagina componente toglie un veicolo riscrivendo solo Componente, quindi la voce
/// residua in Funzionalita.VeicoliPerEcu resta ma è inerte finché il veicolo non torna montato; viene riscritta alla prossima
/// modifica esplicita della restrizione (finestra del veicolo o checklist della pagina Funzionalità), che scrive solo i veicoli
/// montati. La finestra del veicolo scrive entrambi i cataloghi in un batch e toglie il veicolo subito. Limite noto: se il componente
/// torna montata sul veicolo prima di quella riscrittura, la voce residua torna efficace. Chiuderlo del tutto richiederebbe una
/// scrittura su Funzionalità anche dalla pagina componente, cioè un salvataggio su più cataloghi.
/// </summary>
public static class AssociazioniVeicoloService
{
    private static readonly StringComparer Confronto = StringComparer.OrdinalIgnoreCase;

    /// <summary>componente selezionabili nel dialogo: le non archiviate, in ordine naturale (componente2 prima di componente10).</summary>
    public static IReadOnlyList<Componente> EcuSelezionabili(IEnumerable<Componente> componente) =>
        componente.Where(e => !e.Archiviata && !string.IsNullOrWhiteSpace(e.Nome))
           .OrderBy(e => e.Nome.Trim(), NaturalStringComparer.Instance)
           .ToList();

    /// <summary>Vero se il veicolo è tra quelli su cui il componente è montata.</summary>
    public static bool EcuMontataSulVeicolo(Componente componente, string veicolo) =>
        componente.VeicoliAssociati.Contains(veicolo, Confronto);

    /// <summary>Funzionalità attive che valgono sul componente (su tutti i componenti oppure associate a questa), in ordine naturale.</summary>
    public static IReadOnlyList<Funzionalita> FunzionalitaDellEcu(IEnumerable<Funzionalita> funzionalita, string componente) =>
        funzionalita.Where(f => !f.Archiviata && f.ValeSuEcu(componente))
                    .OrderBy(f => f.Nome, NaturalStringComparer.Instance)
                    .ToList();

    /// <summary>Funzionalità attive non ancora associate al componente. Quelle "tutti i componenti" valgono già su ogni componente, quindi non compaiono.</summary>
    public static IReadOnlyList<Funzionalita> FunzionalitaAggiungibili(IEnumerable<Funzionalita> funzionalita, string componente) =>
        funzionalita.Where(f => !f.Archiviata && !f.ValeSuEcu(componente))
                    .OrderBy(f => f.Nome, NaturalStringComparer.Instance)
                    .ToList();

    /// <summary>
    /// Imposta se il componente è montata sul veicolo. Smontare il componente dal veicolo toglie il veicolo da ogni restrizione di
    /// quell'componente, così le altre funzionalità e gli altri veicoli restano invariati. Montarla non cambia le restrizioni.
    /// </summary>
    public static void ImpostaEcuMontata(Componente componente, string veicolo, bool montata, IEnumerable<Funzionalita> funzionalita)
    {
        ArgumentNullException.ThrowIfNull(componente);
        ArgumentNullException.ThrowIfNull(funzionalita);
        if (montata)
        {
            if (!EcuMontataSulVeicolo(componente, veicolo)) componente.VeicoliAssociati.Add(veicolo);
            return;
        }

        componente.VeicoliAssociati.RemoveAll(v => Confronto.Equals(v, veicolo));
        foreach (var f in funzionalita)
        {
            if (!f.VeicoliPerEcu.Any(voce => Confronto.Equals(voce.Componente, componente.Nome))) continue;
            foreach (var voce in f.VeicoliPerEcu.Where(voce => Confronto.Equals(voce.Componente, componente.Nome)))
                voce.Veicoli.RemoveAll(v => Confronto.Equals(v, veicolo));
            f.NormalizzaVeicoliPerEcu();
        }
    }

    /// <summary>
    /// Imposta se la funzionalità vale sul veicolo del componente. Precondizione implicita: la funzionalità deve valere sul componente
    /// (altrimenti non c'è nulla da restringere e il metodo non fa nulla). Senza voce, disattivarla per il veicolo crea una
    /// voce con tutti gli altri veicoli del componente; riattivarla in una voce esistente aggiunge il veicolo, e quando la voce
    /// comprende di nuovo tutti i veicoli del componente viene rimossa (torna "nessuna restrizione").
    /// </summary>
    public static void ImpostaFunzioneSulVeicolo(Funzionalita funzionalita, Componente componente, string veicolo, bool applicata)
    {
        ArgumentNullException.ThrowIfNull(funzionalita);
        ArgumentNullException.ThrowIfNull(componente);
        if (!funzionalita.ValeSuEcu(componente.Nome)) return;

        var veicoliEcu = VeicoliDellEcu(componente);
        var ristretti = funzionalita.VeicoliRistretti(componente.Nome);
        List<string> nuovi;
        if (ristretti == null)
        {
            if (applicata) return; // già su tutti i veicoli del componente
            nuovi = veicoliEcu.Where(v => !Confronto.Equals(v, veicolo)).ToList();
        }
        else
        {
            // Riscrive solo i veicoli ancora montati (ordine della restrizione): le voci residue di veicoli non più montati sono
            // inerti e vengono tolte qui, dove il chiamante ha entrambi i cataloghi freschi.
            var ammessi = ristretti.Where(v => veicoliEcu.Contains(v, Confronto)).Distinct(Confronto).ToList();
            if (applicata == ammessi.Contains(veicolo, Confronto)) return; // già nello stato richiesto
            nuovi = applicata
                ? ammessi.Append(veicolo).ToList()
                : ammessi.Where(v => !Confronto.Equals(v, veicolo)).ToList();
        }
        ImpostaVoceEcu(funzionalita, componente, nuovi, veicoliEcu);
        funzionalita.NormalizzaVeicoliPerEcu();
    }

    /// <summary>
    /// Associa al componente una funzionalità che non vi valeva. Con <paramref name="soloQuestoVeicolo"/> e altri veicoli sul componente,
    /// la funzionalità vale solo sul veicolo (voce con quel veicolo); altrimenti vale su tutti i veicoli del componente (nessuna voce).
    /// Un componente con un solo veicolo non richiede restrizioni.
    /// </summary>
    public static void AggiungiFunzioneAdEcu(Funzionalita funzionalita, Componente componente, string veicolo, bool soloQuestoVeicolo)
    {
        ArgumentNullException.ThrowIfNull(funzionalita);
        ArgumentNullException.ThrowIfNull(componente);
        if (funzionalita.ValeSuEcu(componente.Nome)) return; // già associata, o valida per tutti i componenti

        funzionalita.EcuAssociate ??= new List<string>();
        funzionalita.EcuAssociate.Add(componente.Nome);
        // Eventuali voci residue del componente non devono riattivarsi con la nuova associazione.
        funzionalita.VeicoliPerEcu = funzionalita.VeicoliPerEcu.Where(voce => !Confronto.Equals(voce.Componente, componente.Nome)).ToList();
        var altriVeicoli = VeicoliDellEcu(componente).Any(v => !Confronto.Equals(v, veicolo));
        if (soloQuestoVeicolo && altriVeicoli)
            funzionalita.VeicoliPerEcu.Add(new VeicoliFunzionalitaEcu { Componente = componente.Nome, Veicoli = new List<string> { veicolo } });
        funzionalita.NormalizzaVeicoliPerEcu();
    }

    /// <summary>Veicoli montati sul componente, senza vuoti né duplicati, nell'ordine di Componente.VeicoliAssociati.</summary>
    public static IReadOnlyList<string> VeicoliMontati(Componente componente)
    {
        ArgumentNullException.ThrowIfNull(componente);
        return VeicoliDellEcu(componente);
    }

    /// <summary>Veicoli montati sul componente su cui la funzionalità vale: senza restrizione tutti quelli montati, altrimenti
    /// l'intersezione tra la restrizione e il montaggio, nell'ordine del componente.</summary>
    public static IReadOnlyList<string> VeicoliAmmessi(Funzionalita funzionalita, Componente componente)
    {
        ArgumentNullException.ThrowIfNull(funzionalita);
        ArgumentNullException.ThrowIfNull(componente);
        var montati = VeicoliDellEcu(componente);
        var ristretti = funzionalita.VeicoliRistretti(componente.Nome);
        return ristretti == null ? montati : montati.Where(v => ristretti.Contains(v, Confronto)).ToList();
    }

    /// <summary>Vero se la funzionalità vale sul veicolo del componente, con la restrizione intersecata con il montaggio. Senza
    /// restrizione è come <see cref="Funzionalita.ValeSu"/> (nessun controllo di montaggio, come prima); con restrizione una
    /// voce residua di un veicolo non più montato non vale.</summary>
    public static bool ValeSuVeicoloMontato(Funzionalita funzionalita, Componente componente, string veicolo)
    {
        ArgumentNullException.ThrowIfNull(funzionalita);
        ArgumentNullException.ThrowIfNull(componente);
        if (!funzionalita.ValeSu(componente.Nome, veicolo)) return false;
        if (string.IsNullOrEmpty(veicolo) || funzionalita.VeicoliRistretti(componente.Nome) == null) return true;
        return EcuMontataSulVeicolo(componente, veicolo);
    }

    /// <summary>Quanti dei veicoli montati sul componente ricevono la funzionalità: per il riepilogo "tutti i veicoli" o "N di M".</summary>
    public readonly record struct RiepilogoVeicoliFunzione(int Ammessi, int Montati)
    {
        /// <summary>Vero se la funzionalità vale su tutti i veicoli montati (nessuna restrizione effettiva).</summary>
        public bool TuttiVeicoli => Ammessi == Montati;
    }

    public static RiepilogoVeicoliFunzione RiepilogoVeicoli(Funzionalita funzionalita, Componente componente) =>
        new(VeicoliAmmessi(funzionalita, componente).Count, VeicoliDellEcu(componente).Count);

    /// <summary>Imposta i veicoli su cui la funzionalità vale sul componente, dalla checklist della pagina Funzionalità. Stessa regola di
    /// <see cref="ImpostaFunzioneSulVeicolo"/>: se i veicoli scelti coincidono con tutti quelli montati la voce viene tolta;
    /// altrimenti la voce elenca solo i veicoli scelti ancora montati. Precondizione: la funzionalità vale sul componente (altrimenti
    /// non fa nulla).</summary>
    public static void ImpostaVeicoliFunzione(Funzionalita funzionalita, Componente componente, IEnumerable<string> veicoliScelti)
    {
        ArgumentNullException.ThrowIfNull(funzionalita);
        ArgumentNullException.ThrowIfNull(componente);
        ArgumentNullException.ThrowIfNull(veicoliScelti);
        if (!funzionalita.ValeSuEcu(componente.Nome)) return;

        var scelti = new HashSet<string>(veicoliScelti.Where(v => !string.IsNullOrWhiteSpace(v)), Confronto);
        var veicoliEcu = VeicoliDellEcu(componente);
        ImpostaVoceEcu(funzionalita, componente, veicoliEcu.Where(scelti.Contains).ToList(), veicoliEcu);
        funzionalita.NormalizzaVeicoliPerEcu();
    }

    /// <summary>Veicoli montati sul componente, senza vuoti né duplicati.</summary>
    private static List<string> VeicoliDellEcu(Componente componente) =>
        componente.VeicoliAssociati.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(Confronto).ToList();

    /// <summary>
    /// Scrive i veicoli ammessi per il componente. Se coincidono con tutti i veicoli del componente la voce viene tolta (nessuna
    /// restrizione); altrimenti la voce del componente viene sostituita nello stesso punto dell'elenco, così le altre voci
    /// e il loro ordine restano invariati (niente riordini spurî nel salvataggio).
    /// </summary>
    private static void ImpostaVoceEcu(Funzionalita funzionalita, Componente componente, IEnumerable<string> veicoli, List<string> veicoliEcu)
    {
        var distinti = veicoli.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(Confronto).ToList();
        var voci = funzionalita.VeicoliPerEcu.ToList();
        var posizione = voci.FindIndex(voce => Confronto.Equals(voce.Componente, componente.Nome));
        voci.RemoveAll(voce => Confronto.Equals(voce.Componente, componente.Nome));
        if (!new HashSet<string>(distinti, Confronto).SetEquals(veicoliEcu))
        {
            var voce = new VeicoliFunzionalitaEcu { Componente = componente.Nome, Veicoli = distinti };
            if (posizione >= 0) voci.Insert(Math.Min(posizione, voci.Count), voce);
            else voci.Add(voce);
        }
        funzionalita.VeicoliPerEcu = voci;
    }
}
