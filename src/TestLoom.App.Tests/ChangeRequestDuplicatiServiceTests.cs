using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class ChangeRequestDuplicatiServiceTests
{
    [Fact]
    public void StessoAmbitoEUnaSolaParolaCondivisa_NonSonoSufficientiPerSegnalareDuplicato()
    {
        var archivio = new[]
        {
            new ChangeRequest
            {
                Id = "CR-100", Titolo = "Installare gateway nuovo",
                Descrizione = "Sequenza diagnostica specifica per allestimento urbano",
                VeicoliImpattati = new() { "Daily 24" }, EcuImpattate = new() { "BCM" }
            }
        };
        var importata = new ChangeRequest
        {
            Id = "CR-200", Titolo = "Aggiornare gateway esistente",
            Descrizione = "Calibrazione distinta dei parametri di telemetria",
            VeicoliImpattati = new() { "Daily 24" }, EcuImpattate = new() { "BCM" },
            VinCliente = new() { "Matricola-IMPORTATO" }, FunzionalitaImpattate = new() { "Diagnostica" }
        };

        Assert.Empty(ChangeRequestDuplicatiService.TrovaPossibiliDuplicati(archivio, importata));
    }

    [Fact]
    public void StessoAmbitoEContenutoTecnicoCondiviso_SegnalaDuplicatoPlausibile()
    {
        var esistente = new ChangeRequest
        {
            Id = "CR-101", Titolo = "Integrazione gateway ADAS",
            Descrizione = "Aggiornare comunicazione gateway ADAS sui veicoli Daily",
            VeicoliImpattati = new() { "Daily 24" }, EcuImpattate = new() { "BCM" }
        };
        var candidata = new ChangeRequest
        {
            Id = "CR-201", Titolo = "Integrazione gateway ADAS urgente",
            Descrizione = "Aggiornare comunicazione gateway ADAS per Daily 24",
            VeicoliImpattati = new() { "Daily 24" }, EcuImpattate = new() { "BCM" }
        };

        Assert.Equal(new[] { esistente.Id }, ChangeRequestDuplicatiService.TrovaPossibiliDuplicati(new[] { esistente }, candidata));
    }

    [Fact]
    public void BoilerplateImportatoComuneNonAumentaLaSomiglianzaDelTestoTecnico()
    {
        var archivio = Enumerable.Range(0, 6).Select(i => new ChangeRequest
        {
            Id = $"CR-I{i}", Titolo = "Richiesta aggiornamento sistema",
            Descrizione = "Analisi tecnica da completare dopo la verifica",
            VeicoliImpattati = new() { "Daily 24" }
        }).ToArray();
        var candidata = new ChangeRequest
        {
            Id = "CR-I9", Titolo = "Sostituire protocollo sensore pressione",
            Descrizione = "Ricalibrare algoritmo compensazione termica",
            VeicoliImpattati = new() { "Daily 24" }
        };

        Assert.Empty(ChangeRequestDuplicatiService.TrovaPossibiliDuplicati(archivio, candidata));
    }
}
