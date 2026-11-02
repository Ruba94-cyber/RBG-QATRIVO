using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

[Collection("Localization")]
public class TipoCampoStrumentoDataTests
{
    [Fact]
    public void Label_Data_RestituisceEtichettaData()
    {
        var loc = LocalizationService.Instance;
        var original = loc.Lingua;
        try
        {
            loc.Lingua = "it";
            Assert.Equal("Data", TipoCampoStrumento.Data.Label());
        }
        finally { loc.Lingua = original; }
    }
}
