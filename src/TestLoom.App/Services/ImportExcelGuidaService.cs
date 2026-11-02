namespace TestLoom.App.Services;

/// <summary>Presenta le istruzioni essenziali prima di aprire il selettore dei file Excel.</summary>
public static class ImportExcelGuidaService
{
    public static Task<bool> Chiedi(ImportExcelCatalogo catalogo)
    {
        var loc = LocalizationService.Instance;
        var schema = catalogo switch
        {
            ImportExcelCatalogo.Componente => loc.Traduci("ImportExcelSchemaEcu"),
            ImportExcelCatalogo.Gamma => loc.Traduci("ImportExcelSchemaGamma"),
            ImportExcelCatalogo.Veicoli => loc.Traduci("ImportExcelSchemaVeicoli"),
            ImportExcelCatalogo.Funzionalita => loc.Traduci("ImportExcelSchemaFunzionalita"),
            ImportExcelCatalogo.Requisiti => loc.Traduci("ImportExcelSchemaRequisiti"),
            ImportExcelCatalogo.Strumenti => loc.Traduci("ImportExcelSchemaStrumenti"),
            ImportExcelCatalogo.DtcRequisiti => loc.Traduci("ImportExcelSchemaDtcRequisiti"),
            _ => throw new ArgumentOutOfRangeException(nameof(catalogo), catalogo, null)
        };
        var chiaveTesto = catalogo == ImportExcelCatalogo.DtcRequisiti
            ? "ImportDtcGuidaTestoFmt"
            : "ImportExcelGuidaTestoFmt";
        var messaggio = string.Format(loc.Traduci(chiaveTesto), schema);
        var chiaveTitolo = catalogo == ImportExcelCatalogo.DtcRequisiti
            ? "ImportDtcGuidaTitolo"
            : "ImportExcelGuidaTitolo";
        return ConfirmService.Chiedi(
            loc.Traduci(chiaveTitolo),
            messaggio,
            loc.Traduci("Continua"),
            loc.Traduci("Annulla"));
    }
}

public enum ImportExcelCatalogo
{
    Componente,
    Gamma,
    Veicoli,
    Funzionalita,
    Requisiti,
    Strumenti,
    DtcRequisiti
}
