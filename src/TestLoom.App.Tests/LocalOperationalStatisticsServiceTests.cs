using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class LocalOperationalStatisticsServiceTests
{
    [Fact]
    public void MultiEcuOperation_CountsOneOperationPerEcu()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_Stats", Guid.NewGuid().ToString("N"));
        var local = Path.Combine(Path.GetTempPath(), "TestLoom_StatsLocal", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new LocalTestIndexStore(root, local);
            var tc = new TestCase
            {
                Id = "TC-001",
                Uid = Guid.NewGuid(),
                DataCreazione = DateTime.Now,
                UltimaModifica = DateTime.Now,
                CreatoDa = "Paolo",
                ModificatoDa = "Paolo",
                Componente = new EcuSelection(new[] { "Y", "Z" }),
                Funzionalita = new List<string> { "X", "K" },
                Esecuzioni = new List<Esecuzione>
                {
                    new()
                    {
                        Iterazione = 1,
                        Data = DateTime.Now,
                        Operatore = "Paolo",
                        Esito = Esito.Conforme,
                        Condizioni = new List<Condizione>
                        {
                            new()
                            {
                                Funzionalita = "X",
                                Componente = new EcuSelection(new[] { "Y", "Z" }),
                                Esito = Esito.Conforme
                            },
                            new()
                            {
                                Funzionalita = "K",
                                Componente = new EcuSelection(new[] { "Y" }),
                                Esito = Esito.Conforme
                            }
                        }
                    }
                }
            };

            var path = Path.Combine(root, "testcase_TC-001.json");
            JsonIo.WriteAtomic(path, tc);
            var info = new FileInfo(path);
            store.Upsert(path, info.LastWriteTimeUtc, info.Length, tc);

            var stats = new LocalOperationalStatisticsService(store.DatabasePath);
            var archive = stats.GetArchiveStatistics();
            Assert.Equal(3, archive.OperationAttemptCount);
            Assert.Equal(3, archive.EffectiveOperationCount);

            var paolo = Assert.Single(stats.GetByOperator());
            Assert.Equal("Paolo", paolo.Operator);
            Assert.Equal(3, paolo.OperationAttemptCount);

            var byFunction = stats.GetByFunctionality().ToDictionary(x => x.Functionality);
            Assert.Equal(2, byFunction["X"].TotalOperationAttempts);
            Assert.Equal(2, byFunction["X"].TestFunctionalityEcuPairs);
            Assert.Equal(1, byFunction["K"].TotalOperationAttempts);

            var byEcu = stats.GetByEcu().ToDictionary(x => x.Componente);
            Assert.Equal(2, byEcu["Y"].OperationAttemptCount);
            Assert.Equal(1, byEcu["Z"].OperationAttemptCount);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
            try { Directory.Delete(local, true); } catch { }
        }
    }

    [Fact]
    public void NewRevision_AddsOnlyOperationsActuallyPresentInThatRevision()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_Stats", Guid.NewGuid().ToString("N"));
        var local = Path.Combine(Path.GetTempPath(), "TestLoom_StatsLocal", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new LocalTestIndexStore(root, local);
            var tc = new TestCase
            {
                Id = "TC-002",
                Uid = Guid.NewGuid(),
                DataCreazione = DateTime.Now,
                UltimaModifica = DateTime.Now,
                CreatoDa = "Paolo",
                ModificatoDa = "Paolo",
                Componente = new EcuSelection(new[] { "Y", "Z" }),
                Funzionalita = new List<string> { "X" }
            };
            tc.Esecuzioni.Add(new Esecuzione
            {
                Iterazione = 1,
                Data = DateTime.Now.AddMinutes(-10),
                Operatore = "Paolo",
                Esito = Esito.NonConforme,
                Condizioni = new List<Condizione>
                {
                    new() { Funzionalita = "X", Componente = new EcuSelection(new[] { "Y", "Z" }), Esito = Esito.NonConforme }
                }
            });
            tc.Esecuzioni.Add(new Esecuzione
            {
                Iterazione = 2,
                Data = DateTime.Now,
                Operatore = "Collega",
                Esito = Esito.Conforme,
                Condizioni = new List<Condizione>
                {
                    new() { Funzionalita = "X", Componente = new EcuSelection(new[] { "Y" }), Esito = Esito.Conforme }
                }
            });

            var path = Path.Combine(root, "testcase_TC-002.json");
            JsonIo.WriteAtomic(path, tc);
            var info = new FileInfo(path);
            store.Upsert(path, info.LastWriteTimeUtc, info.Length, tc);

            var stats = new LocalOperationalStatisticsService(store.DatabasePath);
            var archive = stats.GetArchiveStatistics();
            Assert.Equal(3, archive.OperationAttemptCount); // rev1: Y+Z = 2; rev2: solo Y = 1
            Assert.Equal(2, archive.RevisionCount);

            var operators = stats.GetByOperator().ToDictionary(x => x.Operator);
            Assert.Equal(2, operators["Paolo"].OperationAttemptCount);
            Assert.Equal(1, operators["Collega"].OperationAttemptCount);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
            try { Directory.Delete(local, true); } catch { }
        }
    }

    [Fact]
    public void EmptyPlaceholderWithoutEcu_DoesNotCountAsOperation()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_Stats", Guid.NewGuid().ToString("N"));
        var local = Path.Combine(Path.GetTempPath(), "TestLoom_StatsLocal", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new LocalTestIndexStore(root, local);
            var tc = new TestCase
            {
                Id = "TC-003",
                Uid = Guid.NewGuid(),
                DataCreazione = DateTime.Now,
                UltimaModifica = DateTime.Now,
                CreatoDa = "Paolo",
                ModificatoDa = "Paolo",
                Componente = new EcuSelection(new[] { "Y", "Z" }),
                Funzionalita = new List<string> { "X" },
                Esecuzioni = new List<Esecuzione>
                {
                    new()
                    {
                        Iterazione = 1,
                        Data = DateTime.Now,
                        Operatore = "Paolo",
                        Esito = Esito.NonEseguitoNA,
                        Condizioni = new List<Condizione>
                        {
                            new() { Funzionalita = "", Componente = new EcuSelection(), Esito = Esito.NonEseguitoNA }
                        }
                    }
                }
            };

            var path = Path.Combine(root, "testcase_TC-003.json");
            JsonIo.WriteAtomic(path, tc);
            var info = new FileInfo(path);
            store.Upsert(path, info.LastWriteTimeUtc, info.Length, tc);

            var stats = new LocalOperationalStatisticsService(store.DatabasePath);
            Assert.Equal(0, stats.GetArchiveStatistics().OperationAttemptCount);
            Assert.Empty(stats.GetByOperator());
            Assert.Empty(stats.GetByEcu());
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
            try { Directory.Delete(local, true); } catch { }
        }
    }

    [Fact]
    public void OperazioneDaEseguire_NonContaAncheConEcuValorizzata()
    {
        // Decisione utente 6.4.0, punto 1: a differenza di TC-003 sopra (nessuna componente dichiarata), qui il componente
        // C'È — la ragione per cui NON deve contare è unicamente che l'operazione non è stata eseguita.
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_Stats", Guid.NewGuid().ToString("N"));
        var local = Path.Combine(Path.GetTempPath(), "TestLoom_StatsLocal", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new LocalTestIndexStore(root, local);
            var tc = new TestCase
            {
                Id = "TC-005",
                Uid = Guid.NewGuid(),
                DataCreazione = DateTime.Now,
                UltimaModifica = DateTime.Now,
                CreatoDa = "Paolo",
                ModificatoDa = "Paolo",
                Componente = new EcuSelection(new[] { "Y" }),
                Funzionalita = new List<string> { "X" },
                Esecuzioni = new List<Esecuzione>
                {
                    new()
                    {
                        Iterazione = 1,
                        Data = DateTime.Now,
                        Operatore = "Paolo",
                        Condizioni = new List<Condizione>
                        {
                            new() { Funzionalita = "X", Componente = new EcuSelection(new[] { "Y" }), Esito = Esito.DaEseguire },
                            new() { Funzionalita = "X", Componente = new EcuSelection(new[] { "Y" }), Esito = Esito.NonApplicabile }
                        }
                    }
                }
            };

            var path = Path.Combine(root, "testcase_TC-005.json");
            JsonIo.WriteAtomic(path, tc);
            var info = new FileInfo(path);
            store.Upsert(path, info.LastWriteTimeUtc, info.Length, tc);

            var stats = new LocalOperationalStatisticsService(store.DatabasePath);
            Assert.Equal(0, stats.GetArchiveStatistics().OperationAttemptCount);
            Assert.Equal(0, stats.GetArchiveStatistics().EffectiveOperationCount);
            Assert.Empty(stats.GetByOperator());
            Assert.Empty(stats.GetByEcu());
            Assert.Empty(stats.GetByFunctionality());
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
            try { Directory.Delete(local, true); } catch { }
        }
    }

    [Fact]
    public void LegacySingleTestEcu_IsInferredWhenConditionEcuMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_Stats", Guid.NewGuid().ToString("N"));
        var local = Path.Combine(Path.GetTempPath(), "TestLoom_StatsLocal", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new LocalTestIndexStore(root, local);
            var tc = new TestCase
            {
                Id = "TC-004",
                Uid = Guid.NewGuid(),
                DataCreazione = DateTime.Now,
                UltimaModifica = DateTime.Now,
                CreatoDa = "Paolo",
                ModificatoDa = "Paolo",
                Componente = new EcuSelection(new[] { "Y" }),
                Funzionalita = new List<string> { "X" },
                Esecuzioni = new List<Esecuzione>
                {
                    new()
                    {
                        Iterazione = 1,
                        Data = DateTime.Now,
                        Operatore = "Paolo",
                        Esito = Esito.Conforme,
                        Condizioni = new List<Condizione>
                        {
                            // Simula un Test storico creato prima del componente esplicita per operazione.
                            new() { Funzionalita = "X", Componente = new EcuSelection(), Esito = Esito.Conforme }
                        }
                    }
                }
            };

            var path = Path.Combine(root, "testcase_TC-004.json");
            JsonIo.WriteAtomic(path, tc);
            var info = new FileInfo(path);
            store.Upsert(path, info.LastWriteTimeUtc, info.Length, tc);

            var stats = new LocalOperationalStatisticsService(store.DatabasePath);
            Assert.Equal(1, stats.GetArchiveStatistics().OperationAttemptCount);
            Assert.Equal(1, Assert.Single(stats.GetByOperator()).OperationAttemptCount);
            var componente = Assert.Single(stats.GetByEcu());
            Assert.Equal("Y", componente.Componente);
            Assert.Equal(1, componente.OperationAttemptCount);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
            try { Directory.Delete(local, true); } catch { }
        }
    }

    [Fact]
    public void GetByOperator_RevisionCount_ExcludesRevisionsWithoutExecutedOperation()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_Stats", Guid.NewGuid().ToString("N"));
        var local = Path.Combine(Path.GetTempPath(), "TestLoom_StatsLocal", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new LocalTestIndexStore(root, local);
            Esecuzione Rev(int it, params Esito[] esiti) => new()
            {
                Iterazione = it, Data = DateTime.Now, Operatore = "Paolo", Esito = esiti[0],
                Condizioni = esiti.Select(e => new Condizione { Funzionalita = "X", Componente = new EcuSelection(new[] { "Y" }), Esito = e }).ToList()
            };
            var tc = new TestCase
            {
                Id = "TC-NA", Uid = Guid.NewGuid(), DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now,
                CreatoDa = "Paolo", ModificatoDa = "Paolo", Componente = new EcuSelection(new[] { "Y" }),
                Funzionalita = new List<string> { "X" },
                Esecuzioni = new List<Esecuzione> { Rev(1, Esito.NonApplicabile), Rev(2, Esito.DaEseguire), Rev(3, Esito.NonApplicabile, Esito.Conforme) }
            };
            var path = Path.Combine(root, "testcase_TC-NA.json");
            JsonIo.WriteAtomic(path, tc);
            var info = new FileInfo(path);
            store.Upsert(path, info.LastWriteTimeUtc, info.Length, tc);

            var paolo = Assert.Single(new LocalOperationalStatisticsService(store.DatabasePath).GetByOperator());
            Assert.Equal(1, paolo.RevisionCount);
            Assert.Equal(1, paolo.OperationAttemptCount);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
            try { Directory.Delete(local, true); } catch { }
        }
    }
}
