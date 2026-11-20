namespace Ledger.Application.Dto;

public sealed record KeyChangesDto
{
    public required List<byte[]> Added { get; init; }
    public required List<KeyGrantDto> InvitesAdded { get; init; }
    public required List<byte[]> Removed { get; init; }
    public required List<KeyGrantDto> RolesChanged { get; init; }
}
