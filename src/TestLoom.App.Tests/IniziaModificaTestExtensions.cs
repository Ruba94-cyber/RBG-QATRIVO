using CommunityToolkit.Mvvm.Input;

namespace TestLoom.App.Tests;

/// <summary>Helper per i test che avviano la modifica in modo sincrono. IniziaModifica è asincrono (il lock si
/// acquisisce fuori dal thread UI): il comando gira su un thread del pool, senza SynchronizationContext, così la
/// ripresa dell'await non dipende dal contesto del test. Senza questo, un test <c>async Task</c> che bloccasse sul
/// Task deadlockerebbe, perché xUnit pompa il proprio contesto solo per il test stesso.</summary>
internal static class IniziaModificaTestExtensions
{
    internal static void AttendiModifica(this IAsyncRelayCommand comando) =>
        Task.Run(() => comando.ExecuteAsync(null)).GetAwaiter().GetResult();
}
