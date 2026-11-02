using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Chiave funzionale usata SOLO per vincoli di unicita', diagnostica e bootstrap deterministico Phase D.
/// Non e' l'identita' dell'entita': rename e correzioni della chiave business mantengono lo stesso Uid tecnico.
/// </summary>
public static class PhaseDMasterBusinessKeyService
{
    public static string Get(MasterEntity item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var key = item switch
        {
            Requisito x => x.Id,
            DtcArchivioRequisito x => x.Id,
            Funzionalita x => x.Nome,
            Componente x => x.Nome,
            Gamma x => x.Nome,
            Categoria x => x.Nome,
            Veicolo x => x.Nome,
            StrumentoDiagnostico x => x.Nome,
            Operatore x => string.IsNullOrWhiteSpace(x.Iniziali) ? x.Nome : x.Iniziali,
            TestTemplate x => x.Nome,
            CampoSistemaDefinizione x => x.Chiave,
            RevisioneUdt x => x.WorkingUnitRev,
            RisoluzioneConflittoAudit x => x.Componente + "|" + x.RequisitoId,
            ValutazioneRequisitoEcuVeicolo x => x.RequisitoId + "|" + x.Componente + "|" + x.Veicolo,
            StatoFunzionalitaEcuVeicolo x => x.Funzionalita + "|" + x.Componente + "|" + x.Veicolo,
            _ => ReflectBusinessKey(item)
        };
        return key?.Trim() ?? "";
    }

    private static string ReflectBusinessKey(MasterEntity item)
    {
        foreach (var propertyName in new[] { "Chiave", "Nome", "Id" })
        {
            var property = item.GetType().GetProperty(propertyName);
            if (property?.PropertyType == typeof(string) && property.GetValue(item) is string value && !string.IsNullOrWhiteSpace(value))
                return value;
        }
        return "";
    }
}
