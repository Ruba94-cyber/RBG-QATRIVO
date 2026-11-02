using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.WindowsAPI;

namespace TestLoom.App.UITests.Tests;

/// <summary>Pulsante "← Indietro" e Alt+← nella vera UI: ritorno alla sezione precedente senza perdere la pagina in cache.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Navigation")]
[Trait("Category", "Regression")]
public sealed class NavigazioneIndietroUiTests : UiTestBase
{
    public NavigazioneIndietroUiTests()
        : base(TestWorkspace.CreateLoggedIn(nameof(NavigazioneIndietroUiTests))) { }

}
