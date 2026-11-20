using Ledger.Domain.Enums;

namespace Ledger.Application.Dto;

public sealed record KeyGrantDto(byte[] PublicKey, WriteKeyRole Role);
