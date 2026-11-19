using Ledger.Domain.Enums;

namespace Ledger.API.Requests;

internal sealed record KeyGrantRequest(byte[] PublicKey, WriteKeyRole Role);
