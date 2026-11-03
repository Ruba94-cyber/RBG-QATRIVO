namespace TestLoom.App.UITests.Infrastructure;

/// <summary>
/// Tutti i test UI FlaUI condividono questa collection per essere serializzati (Fase 6): competono per
/// focus/mouse/tastiera/finestre di un solo desktop reale, quindi non possono correre in parallelo tra loro
/// né con qualunque altro test che apra RBG - QATRIVO.exe (il mutex single-instance lo impedirebbe comunque, ma
/// serializzare esplicitamente rende il motivo leggibile invece di manifestarsi come un timeout misterioso).
/// </summary>
[CollectionDefinition("TestLoom UI", DisableParallelization = true)]
public sealed class TestLoomUiCollection { }
