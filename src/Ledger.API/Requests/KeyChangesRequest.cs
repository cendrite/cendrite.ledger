namespace Ledger.API.Requests;

internal sealed record KeyChangesRequest
{
    public required List<byte[]> Added { get; init; }
    public required List<KeyGrantRequest> InvitesAdded { get; init; }
    public required List<byte[]> Removed { get; init; }
    public required List<KeyGrantRequest> RolesChanged { get; init; }
}
