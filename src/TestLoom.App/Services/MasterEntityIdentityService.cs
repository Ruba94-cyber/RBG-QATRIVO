using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Identita' tecnica stabile dei master data per Phase D. Se il modello possiede gia' una proprieta' pubblica
/// Guid Uid (Funzionalita, TestTemplate e qualunque modello futuro) quella e' l'identita' canonica. Gli altri
/// MasterEntity conservano l'identita' in JsonExtensionData con la chiave riservata _ditmUid. In questo modo
/// rename delle chiavi business (Nome/Id/WorkingUnitRev/...) non cambiano l'identita' e il nuovo protocollo
/// distribuito puo' distinguere update, delete e creazione senza affidarsi al testo visibile all'utente.
///
/// L'app non e' ancora deployata: il bootstrap puo' assegnare deterministicamente l'Uid mancante da catalogo +
/// chiave business. Se due PC importano per errore la stessa fotografia legacy su repliche indipendenti, la
/// stessa entita riceve quindi lo stesso Uid e converge invece di apparire come due creazioni distinte.
/// </summary>
public static class MasterEntityIdentityService
{
    public const string ExtensionKey = "_ditmUid";
    private static readonly Dictionary<Type, PropertyInfo?> NativeUidProperties = new();
    private static readonly object NativeUidGate = new();

    public static Guid GetOrCreate(MasterEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (TryGet(entity, out var existing)) return existing;
        var uid = Guid.NewGuid();
        Set(entity, uid);
        return uid;
    }

    /// <summary>
    /// Usa esclusivamente durante bootstrap/import prima del cutover. Un Uid gia realmente persistito viene
    /// conservato; se il formato legacy non ne portava uno, il caller puo forzare l'identita deterministica
    /// passando hadPersistedIdentity=false anche per modelli con initializer Guid.NewGuid().
    /// </summary>
    public static Guid GetOrCreateForBootstrap(
        MasterEntity entity,
        string catalog,
        string businessKey,
        bool hadPersistedIdentity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(businessKey);

        if (hadPersistedIdentity && TryGet(entity, out var persisted)) return persisted;
        var uid = DeterministicGuid("testloom-phase-d-master-v1|" + catalog.Trim().ToLowerInvariant() + "|" + businessKey.Trim().ToUpperInvariant());
        Set(entity, uid);
        return uid;
    }

    public static bool TryGet(MasterEntity entity, out Guid uid)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var native = NativeUidProperty(entity.GetType());
        if (native != null)
        {
            uid = native.GetValue(entity) is Guid value ? value : Guid.Empty;
            return uid != Guid.Empty;
        }

        uid = Guid.Empty;
        if (entity.ExtensionData == null ||
            !entity.ExtensionData.TryGetValue(ExtensionKey, out var valueElement) ||
            valueElement.ValueKind != JsonValueKind.String)
            return false;

        return Guid.TryParseExact(valueElement.GetString(), "N", out uid) || Guid.TryParse(valueElement.GetString(), out uid);
    }

    public static Guid GetRequired(MasterEntity entity)
    {
        if (TryGet(entity, out var uid) && uid != Guid.Empty) return uid;
        throw new InvalidDataException($"Master entity '{entity.GetType().Name}' senza Uid tecnico Phase D.");
    }

    public static void EnsureUnique<T>(IEnumerable<T> items, bool assignMissing = true) where T : MasterEntity
    {
        ArgumentNullException.ThrowIfNull(items);
        var seen = new HashSet<Guid>();
        foreach (var item in items)
        {
            var uid = assignMissing ? GetOrCreate(item) : GetRequired(item);
            if (!seen.Add(uid))
                throw new InvalidDataException($"Uid master duplicato '{uid:N}' nel catalogo {typeof(T).Name}.");
        }
    }

    private static void Set(MasterEntity entity, Guid uid)
    {
        if (uid == Guid.Empty) throw new ArgumentException("Uid Phase D vuoto.", nameof(uid));
        var native = NativeUidProperty(entity.GetType());
        if (native != null)
        {
            native.SetValue(entity, uid);
            return;
        }

        entity.ExtensionData ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        entity.ExtensionData[ExtensionKey] = JsonSerializer.SerializeToElement(uid.ToString("N"));
    }

    private static Guid DeterministicGuid(string source)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        // UUID-compatible variant/version bits; il byte layout resta deterministico all'interno di .NET.
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    private static PropertyInfo? NativeUidProperty(Type type)
    {
        lock (NativeUidGate)
        {
            if (NativeUidProperties.TryGetValue(type, out var cached)) return cached;
            var property = type.GetProperty("Uid", BindingFlags.Instance | BindingFlags.Public);
            if (property?.PropertyType != typeof(Guid) || !property.CanRead || !property.CanWrite) property = null;
            NativeUidProperties[type] = property;
            return property;
        }
    }
}
