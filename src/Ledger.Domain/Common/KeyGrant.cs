using Ledger.Domain.Enums;

namespace Ledger.Domain.Common;

public sealed record KeyGrant(byte[] PublicKey, WriteKeyRole Role);
