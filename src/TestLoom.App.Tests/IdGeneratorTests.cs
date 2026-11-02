using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Copertura del punto 19 (feedback utente 2026-08-20): ID brevi e leggibili "T-0001" progressivi,
/// non più componente-data-iniziali-progressivo — "puramente un identificativo utile", il componente/data/operatore si
/// leggono già dalle colonne della tabella. Le iniziali dell'operatore sono tornate come prefisso "T-OPA-0001"
/// (feedback utente 2026-08-24, punto 2: rischio di ID duplicati creati "in parallelo" da due operatori prima
/// che una cartella condivisa disallineata si risincronizzi) — il progressivo resta globale su tutti gli ID
/// esistenti, in entrambi i formati; gli ID vecchi non vengono rinominati.</summary>
public class IdGeneratorTests
{
    [Fact]
    public void Genera_NessunIdEsistente_PrimoProgressivo()
    {
        var id = IdGenerator.Genera(Array.Empty<string>(), "OPA");
        Assert.Equal("T-OPA-0001", id);
    }

    [Fact]
    public void Genera_ConIdEsistenti_IncrementaOltreIlMassimo()
    {
        var esistenti = new[] { "T-0001", "T-0002" };
        var id = IdGenerator.Genera(esistenti, "OPA");
        Assert.Equal("T-OPA-0003", id);
    }

    [Fact]
    public void Genera_IgnoraIdConFormatoDiverso()
    {
        // ID legacy (vecchio formato componente-data-iniziali) o comunque non conformi: non devono interferire col
        // calcolo del progressivo, ignorati invece di far esplodere il parsing.
        var esistenti = new[] { "Componente-TEST-BCM-20260813-OPA-05", "qualcosa-di-strano" };
        var id = IdGenerator.Genera(esistenti, "OPA");
        Assert.Equal("T-OPA-0001", id);
    }

    [Fact]
    public void Genera_ProgressivoGlobaleAncheConIdDiAltriOperatori()
    {
        // Il progressivo conta su tutti i test esistenti indipendentemente da chi li ha creati: le iniziali
        // sono solo un tag distintivo, non una sequenza separata per operatore.
        var esistenti = new[] { "T-OPA-0001", "T-OPB-0002" };
        var id = IdGenerator.Genera(esistenti, "OPC");
        Assert.Equal("T-OPC-0003", id);
    }

    [Fact]
    public void RegeneraProgressivo_IncrementaDiUno()
    {
        Assert.Equal("T-0002", IdGenerator.RegeneraProgressivo("T-0001"));
    }

    [Fact]
    public void RegeneraProgressivo_PreservaLeIniziali()
    {
        Assert.Equal("T-OPA-0002", IdGenerator.RegeneraProgressivo("T-OPA-0001"));
    }

    [Fact]
    public void RegeneraProgressivo_ChiudeLaCorsaSuCollisioniMultiple()
    {
        // simula più tentativi in sequenza come farebbe TestCaseRepository.Save in caso di collisioni ripetute
        var id = "T-0001";
        id = IdGenerator.RegeneraProgressivo(id);
        id = IdGenerator.RegeneraProgressivo(id);
        id = IdGenerator.RegeneraProgressivo(id);
        Assert.Equal("T-0004", id);
    }

    [Fact]
    public void RegeneraProgressivo_IdNonConforme_RipartesDa0002()
    {
        Assert.Equal("T-0002", IdGenerator.RegeneraProgressivo("qualcosa-di-strano"));
    }

    [Fact]
    public void IniziliDa_PrendeLaPrimaLetteraDiOgniParola()
    {
        Assert.Equal("MRB", IdGenerator.IniziliDa("Mario Rossi Bianchi"));
    }
}
