namespace TestLoom.App.ViewModels;

public partial class NonConformitaViewModel
{
    private int _polarionGridVersion;
    /// <summary>Signals completed background TICKET row projections; no new NC/CR query is run.</summary>
    public int PolarionGridVersion => _polarionGridVersion;

    public void NotificaAggiornamentoCacheEsta()
    {
        unchecked { _polarionGridVersion++; }
        OnPropertyChanged(nameof(PolarionGridVersion));
    }
}
