namespace Ledger.Domain.Common;

public sealed record KeyChanges(
    IReadOnlyCollection<byte[]> Added,
    IReadOnlyCollection<KeyGrant> InvitesAdded,
    IReadOnlyCollection<byte[]> Removed,
    IReadOnlyCollection<KeyGrant> RolesChanged)
{
    public static readonly KeyChanges None = new([], [], [], []);
}
