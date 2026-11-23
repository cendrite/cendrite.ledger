using Ledger.Domain.Abstractions;

namespace Ledger.Domain.Entities;

public sealed class LedgerRemovedKey : Entity
{
    public Guid StreamId { get; private set; }
    public byte[] PublicKey { get; private set; } = [];
    public int RemovedAtVersion { get; private set; }

    private LedgerRemovedKey(Guid id) : base(id) { }

    internal LedgerRemovedKey(Guid ledgerId, byte[] publicKey, int removedAtVersion) : base(Guid.NewGuid())
    {
        StreamId = ledgerId;
        PublicKey = publicKey;
        RemovedAtVersion = removedAtVersion;
    }

    internal void RemovedAgainAt(int version) => RemovedAtVersion = version;
}
