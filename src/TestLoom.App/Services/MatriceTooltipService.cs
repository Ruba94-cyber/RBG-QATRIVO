namespace TestLoom.App.Services;

/// <summary>Spiegazioni contestuali dei valori mostrati nella matrice Report, condivise dalla UI e dall'export Excel.</summary>
public static class MatriceTooltipService
{
    public static string DescrizioneModalita(ModalitaCellaMatrice modalita)
    {
        var chiave = modalita switch
        {
            ModalitaCellaMatrice.Stato => "MatriceTooltipModalitaStato",
            ModalitaCellaMatrice.TestPercentuale => "MatriceTooltipModalitaTestPercentuale",
            ModalitaCellaMatrice.TempoTotale => "MatriceLegendaTempoTotale",
            ModalitaCellaMatrice.TempoMedio => "MatriceLegendaTempoMedio",
            _ => "MatriceTooltipModalitaStatoPercentuale",
        };
        var descrizione = LocalizationService.Instance.Traduci(chiave);
        return ReportMatriceService.ModalitaTempo(modalita)
            ? string.Join(Environment.NewLine, descrizione, LocalizationService.Instance.Traduci("LegendaTempoMatrice"))
            : descrizione;
    }

    public static string DescrizioneStato(StatiMatrice stati, IReadOnlySet<StatoCellaMatrice>? nascosti = null)
    {
        if (stati.Codice == StatoCellaMatrice.Misto)
        {
            var riepilogo = stati.RiepilogoVisibile(nascosti);
            return riepilogo.Length == 0 ? "" : string.Format(LocalizationService.Instance.Traduci("MatriceTooltipValoreMisto"), riepilogo);
        }
        if (nascosti?.Contains(stati.Codice) == true) return "";
        return DescrizioneStato(stati.Codice);
    }

    public static string DescrizioneStato(StatoCellaMatrice stato)
    {
        var descrizione = LocalizationService.Instance.Traduci(stato switch
        {
            StatoCellaMatrice.Vuota => "MatriceTooltipValoreVuoto",
            StatoCellaMatrice.Conforme => "MatriceTooltipValoreConforme",
            StatoCellaMatrice.NonConforme => "MatriceTooltipValoreNonConforme",
            StatoCellaMatrice.ConformeConDeviazioni => "MatriceTooltipValoreDeviazioni",
            StatoCellaMatrice.DaEseguire => "MatriceTooltipValoreDaEseguire",
            StatoCellaMatrice.NonTestato => "MatriceTooltipValoreNonTestato",
            StatoCellaMatrice.DaDecidere => "MatriceTooltipValoreDaDecidere",
            StatoCellaMatrice.Misto => "MatriceTooltipValoreMisto",
            _ => "MatriceTooltipValoreVuoto",
        });
        return stato == StatoCellaMatrice.Misto
            ? string.Format(descrizione, LocalizationService.Instance.Traduci("MatriceTooltipMistoGenerico"))
            : descrizione;
    }

    public static string TooltipCella(CellaMatriceReport cella)
    {
        var righe = new List<string>();
        var modalita = cella.ModalitaTempo ?? (cella.ModalitaStato
            ? ModalitaCellaMatrice.Stato
            : cella.MostraConteggioTest ? ModalitaCellaMatrice.TestPercentuale : ModalitaCellaMatrice.Percentuale);
        righe.Add(DescrizioneModalita(modalita));
        if (cella.NonApplicabile)
        {
            var nota = string.IsNullOrWhiteSpace(cella.NotaNonApplicabile)
                ? LocalizationService.Instance.Traduci("MatriceCellaNATooltip")
                : cella.NotaNonApplicabile;
            righe.Add(nota);
            righe.Add(LocalizationService.Instance.Traduci("MatriceTooltipValoreNA"));
        }
        else if (modalita == ModalitaCellaMatrice.Stato)
        {
            righe.Add(DescrizioneStato(cella.Stati, cella.StatiNascostiPresentazione));
        }
        if (cella.Manuali > 0) righe.Add(LocalizationService.Instance.Traduci("MatriceTooltipManuale"));
        if (cella.ManualiSuperati > 0) righe.Add(LocalizationService.Instance.Traduci("MatriceTooltipManualeSuperato"));
        if (!string.IsNullOrWhiteSpace(cella.TooltipManuale)) righe.Add(cella.TooltipManuale);
        return string.Join(Environment.NewLine, righe.Distinct(StringComparer.Ordinal));
    }

    public static string TooltipTotalePercentuale(TotaleMatriceReport totale)
    {
        var modalita = totale.ModalitaTempo ?? (totale.MostraConteggioTest
            ? ModalitaCellaMatrice.TestPercentuale : ModalitaCellaMatrice.Percentuale);
        return DescrizioneModalita(modalita);
    }

    public static string TooltipTotaleStato(TotaleMatriceReport totale) =>
        string.Join(Environment.NewLine,
            DescrizioneModalita(ModalitaCellaMatrice.Stato),
            DescrizioneStato(totale.Stati, totale.StatiNascostiPresentazione));

    public static string DescrizioneEsito(TestLoom.App.Models.Esito? esito) =>
        LocalizationService.Instance.Traduci(esito switch
        {
            TestLoom.App.Models.Esito.DaEseguire => "HomeTooltipEsitoDaEseguire",
            TestLoom.App.Models.Esito.Conforme => "HomeTooltipEsitoConforme",
            TestLoom.App.Models.Esito.NonConforme => "HomeTooltipEsitoNonConforme",
            TestLoom.App.Models.Esito.ConformeConDeviazioni => "HomeTooltipEsitoDeviazioni",
            TestLoom.App.Models.Esito.NonApplicabile => "HomeTooltipEsitoNA",
            TestLoom.App.Models.Esito.NonEseguitoNA => "HomeTooltipEsitoLegacyNA",
            TestLoom.App.Models.Esito.DaRivedere => "HomeTooltipEsitoDaRivedere",
            _ => "HomeTooltipEsitoNonTestato",
        });
}
