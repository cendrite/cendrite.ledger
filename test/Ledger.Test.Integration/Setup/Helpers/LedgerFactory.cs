using Ledger.Domain.Aggregates;
using Ledger.Domain.Common;
using Ledger.Infrastructure.Persistence.Context;
using Microsoft.EntityFrameworkCore;
using NSec.Cryptography;

namespace Ledger.Test.Integration.Setup.Helpers;

public static class LedgerFactory
{
    public static readonly byte[] GenesisHash = new byte[32];
    public const int GenesisVersion = 1;

    public static async Task<LedgerStream> SeedAsync(LedgerDbContext db, Key creator, byte[]? payload = null, IReadOnlyCollection<KeyGrant>? inviteKeys = null)
    {
        payload ??= [1, 2, 3];
        inviteKeys ??= [];
        var id = Guid.NewGuid();

        var ledger = LedgerStream.Initialize(
            id: id,
            creatorWriteKey: LedgerSigner.PublicKeyOf(creator),
            payload: payload,
            eventhash: LedgerSigner.Hash(id, GenesisVersion, GenesisHash, payload, new KeyChanges([], inviteKeys, [], [])),
            signature: LedgerSigner.Sign(creator, id, GenesisVersion, GenesisHash, payload, new KeyChanges([], inviteKeys, [], [])),
            inviteKeysAdded: inviteKeys).RequiredValue;

        await db.LedgerStream.AddAsync(ledger);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return ledger;
    }

    public static async Task<LedgerStream> ReloadAsync(LedgerDbContext db, Guid id) =>
        await db.LedgerStream.AsNoTracking().Include(l => l.WriteKeys).FirstAsync(l => l.Id == id);
}
