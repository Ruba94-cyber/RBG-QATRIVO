using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class DomainRepositoryDeepRefreshTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_DomainDeepRefresh_" + Guid.NewGuid().ToString("N"));

    public DomainRepositoryDeepRefreshTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public void NonConformita_DeepValidationRilevaContenutoDiversoConStessoTimestamp()
    {
        var paths = CreaPaths();
        var repo = new NonConformitaRepository(paths, TimeSpan.Zero);
        var nc = new NonConformita
        {
            Id = "NC-0001",
            TestCaseId = "TC-1",
            Descrizione = "versione-A",
            DataApertura = DateTime.Now,
            Severita = SeveritaAnomalia.High
        };
        repo.Save(nc);
        repo.RefreshIndex();

        var path = paths.NonConformitaFile(nc.Id);
        var openingMtime = File.GetLastWriteTimeUtc(path);
        var openingLength = new FileInfo(path).Length;
        var remoto = JsonIo.TryRead<NonConformita>(path, out _)!;
        remoto.Descrizione = "versione-B";
        JsonIo.WriteAtomic(path, remoto, overwrite: true);
        Assert.Equal(openingLength, new FileInfo(path).Length);
        File.SetLastWriteTimeUtc(path, openingMtime);

        repo.RefreshIndex();

        Assert.Equal("versione-B", repo.GetById(nc.Id)!.Descrizione);
    }

    [Fact]
    public void ChangeRequest_DeepValidationRilevaContenutoDiversoConStessoTimestamp()
    {
        var paths = CreaPaths();
        var repo = new ChangeRequestRepository(paths, TimeSpan.Zero);
        var cr = new ChangeRequest
        {
            Id = "CR-2026-0001",
            Titolo = "versione-A",
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now
        };
        repo.Save(cr);
        repo.RefreshIndex();

        var path = paths.ChangeRequestFile(cr.Id);
        var openingMtime = File.GetLastWriteTimeUtc(path);
        var openingLength = new FileInfo(path).Length;
        var remoto = JsonIo.TryRead<ChangeRequest>(path, out _)!;
        remoto.Titolo = "versione-B";
        JsonIo.WriteAtomic(path, remoto, overwrite: true);
        Assert.Equal(openingLength, new FileInfo(path).Length);
        File.SetLastWriteTimeUtc(path, openingMtime);

        repo.RefreshIndex();

        Assert.Equal("versione-B", repo.GetById(cr.Id)!.Titolo);
    }

    [Fact]
    public void Segnalazione_DeepValidationRilevaContenutoDiversoConStessoTimestamp()
    {
        var paths = CreaPaths();
        var repo = new SegnalazioneRepository(paths, TimeSpan.Zero);
        var segnalazione = new SegnalazioneDaClassificare
        {
            TestId = "TC-2",
            CondizioneUid = Guid.NewGuid(),
            EsecuzioneIterazione = 1,
            Stato = StatoSegnalazione.DaClassificare,
            MotivazioneEsclusione = "versione-A",
            CreataIl = DateTime.Now
        };
        repo.Save(segnalazione);
        repo.RefreshIndex();

        var path = paths.SegnalazioneFile(segnalazione.Uid);
        var openingMtime = File.GetLastWriteTimeUtc(path);
        var openingLength = new FileInfo(path).Length;
        var remoto = JsonIo.TryRead<SegnalazioneDaClassificare>(path, out _)!;
        remoto.MotivazioneEsclusione = "versione-B";
        JsonIo.WriteAtomic(path, remoto, overwrite: true);
        Assert.Equal(openingLength, new FileInfo(path).Length);
        File.SetLastWriteTimeUtc(path, openingMtime);

        repo.RefreshIndex();

        Assert.Equal("versione-B", repo.Get(segnalazione.Uid)!.MotivazioneEsclusione);
    }

    private DataPaths CreaPaths()
    {
        var paths = new DataPaths(_dir);
        paths.EnsureStructure();
        return paths;
    }
}
