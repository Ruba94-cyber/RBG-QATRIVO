namespace TestLoom.App.Services;

/// <summary>
/// Un editor che possiede stato locale recuperabile può preparare la cleanup dopo una conferma esplicita di
/// abbandono. La conferma è globale perché il dialogo appartiene alla finestra principale; il participant deve
/// quindi restare conservativo e cancellare dati soltanto quando può dimostrare che la propria sessione è
/// effettivamente terminata.
/// </summary>
public interface IConfirmedAbandonmentParticipant
{
    void PrepareConfirmedAbandonmentCleanup();
}
