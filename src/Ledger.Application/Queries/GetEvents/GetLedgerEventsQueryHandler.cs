using Ledger.Application.Abstractions;
using Ledger.Application.Dto;
using Ledger.Application.Interfaces;
using Ledger.Domain.Aggregates;
using Ledger.Domain.Common;

namespace Ledger.Application.Queries.GetEvents;

public sealed class GetLedgerEventsQueryHandler(
    ILedgerRepository ledgerRepository,
    ISignatureHelper signatureHelper)
    : IQueryHandler<GetLedgerEventsQuery, LedgerEventsDto>
{
    public async ValueTask<Result<LedgerEventsDto>> Handle(GetLedgerEventsQuery q, CancellationToken ct)
    {
        var ledger = await ledgerRepository.GetAsync(new LedgerWithWriteKeysSpec(q.LedgerId), ct);

        if (ledger?.ReadableUpTo(q.WriteKeyPublic) is not { } readableUpTo
            || !signatureHelper.IsValidReadSignature(q.LedgerId, q.Timestamp, q.WriteKeyPublic, q.Signature))
            return Result.Fail<LedgerEventsDto>(Error.NotFound<LedgerStream>(q.LedgerId.ToString()));

        var events = await ledgerRepository.GetEventsAsync(q.LedgerId, q.FromVersion, q.Limit + 1, ct);
        var readable = events.Where(e => e.Version <= readableUpTo).ToList();

        return Result.Ok(new LedgerEventsDto
        {
            Version = readableUpTo,
            Archived = ledger.Archived && ledger.HasWriteKey(q.WriteKeyPublic),
            HasMore = readable.Count > q.Limit,
            Events = [.. readable.Take(q.Limit).Select(LedgerEventDto.MapFrom)],
        });
    }
}
