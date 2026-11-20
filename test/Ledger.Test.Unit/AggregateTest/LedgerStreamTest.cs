using FluentAssertions;
using Ledger.Domain.Aggregates;
using Ledger.Domain.Common;
using Ledger.Domain.Entities;
using Ledger.Domain.Enums;

namespace Ledger.Test.Unit.AggregateTest;

public class LedgerStreamTest
{
    private static readonly byte[] CreatorKey = Bytes(0xA1, 32);
    private static readonly byte[] OtherKey = Bytes(0xB2, 32);
    private static readonly byte[] UnknownKey = Bytes(0xC3, 32);

    private static byte[] Bytes(byte value, int length) => [.. Enumerable.Repeat(value, length)];

    private static Result<LedgerEvent> Append(
        LedgerStream ledger,
        byte[] writeKey,
        byte[] eventHash,
        byte[][]? added = null,
        byte[][]? removed = null,
        int? expectedVersion = null,
        byte[]? previousHash = null,
        KeyGrant[]? invites = null,
        KeyGrant[]? roles = null)
    {
        return ledger.Append(
            expectedVersion ?? ledger.Version,
            previousHash ?? ledger.Hash,
            eventHash,
            [1, 2, 3],
            writeKey,
            Bytes(0x00, 64),
            new KeyChanges(added ?? [], invites ?? [], removed ?? [], roles ?? []));
    }

    private static Result<LedgerEvent> Archive(
        LedgerStream ledger,
        byte[] writeKey,
        byte[] eventHash,
        int? expectedVersion = null,
        byte[]? previousHash = null)
    {
        return ledger.Archive(
            expectedVersion ?? ledger.Version,
            previousHash ?? ledger.Hash,
            eventHash,
            [1, 2, 3],
            writeKey,
            Bytes(0x00, 64));
    }

    private static void ShouldBeUnchanged(LedgerStream ledger, int version, byte[] hash, int events, params byte[][] keys)
    {
        ledger.Version.Should().Be(version);
        ledger.Hash.Should().Equal(hash);
        ledger.Events.Should().HaveCount(events);
        ledger.WriteKeys.Should().HaveCount(keys.Length);
        foreach (var key in keys)
            ledger.HasWriteKey(key).Should().BeTrue();
    }

    [Fact]
    public void Constructor_ShouldStartAtVersionZero_WithCreatorKey()
    {
        var ledger = new LedgerStream(Guid.NewGuid(), CreatorKey);

        ledger.Version.Should().Be(0);
        ledger.Hash.Should().Equal(new byte[32]);
        ledger.Events.Should().BeEmpty();
        ledger.Archived.Should().BeFalse();
        ledger.WriteKeys.Should().ContainSingle();
        ledger.HasWriteKey(CreatorKey).Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void Constructor_ShouldThrow_WhenKeyIsNot32Bytes(int length)
    {
        var act = () => new LedgerStream(Guid.NewGuid(), Bytes(0xA1, length));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Append_ShouldCreateVersionOne_WhenFirstAppend()
    {
        var id = Guid.NewGuid();
        var ledger = new LedgerStream(id, CreatorKey);

        var result = Append(ledger, CreatorKey, Bytes(0x01, 32));

        result.Success.Should().BeTrue();
        var evt = result.RequiredValue;
        evt.Version.Should().Be(1);
        evt.StreamId.Should().Be(id);
        evt.PreviousHash.Should().Equal(new byte[32]);
        evt.WriteKeyPublic.Should().Equal(CreatorKey);
        ledger.Events.Should().ContainSingle().Which.Should().BeSameAs(evt);
    }

    [Fact]
    public void Append_ShouldAdvanceHead_WhenSuccessful()
    {
        var ledger = new LedgerStream(Guid.NewGuid(), CreatorKey);
        var eventHash = Bytes(0x01, 32);

        Append(ledger, CreatorKey, eventHash);

        ledger.Version.Should().Be(1);
        ledger.Hash.Should().Equal(eventHash);
    }

    [Fact]
    public void Append_ShouldChainEvents_WhenAppendedSequentially()
    {
        var ledger = new LedgerStream(Guid.NewGuid(), CreatorKey);
        var firstHash = Bytes(0x01, 32);
        var secondHash = Bytes(0x02, 32);

        var first = Append(ledger, CreatorKey, firstHash);
        var second = Append(ledger, CreatorKey, secondHash);

        first.RequiredValue.Version.Should().Be(1);
        second.RequiredValue.Version.Should().Be(2);
        second.RequiredValue.PreviousHash.Should().Equal(firstHash);
        ledger.Version.Should().Be(2);
        ledger.Hash.Should().Equal(secondHash);
        ledger.Events.Should().HaveCount(2);
    }

    [Fact]
    public void Append_ShouldRecordKeyMutations_OnEvent()
    {
        var ledger = new LedgerStream(Guid.NewGuid(), CreatorKey);
        Append(ledger, CreatorKey, Bytes(0x01, 32), invites: [Writer(InviteKey)]);

        var swap = Append(ledger, InviteKey, Bytes(0x02, 32), added: [OtherKey], removed: [InviteKey]);
        var role = Append(ledger, CreatorKey, Bytes(0x03, 32), roles: [Admin(OtherKey)]);

        var invite = ledger.Events.First().InvitesAdded.Should().ContainSingle().Subject;
        invite.PublicKey.Should().Equal(InviteKey);
        invite.Role.Should().Be(WriteKeyRole.Writer);
        swap.RequiredValue.KeysAdded.Should().ContainSingle().Which.Should().Equal(OtherKey);
        swap.RequiredValue.KeysRemoved.Should().ContainSingle().Which.Should().Equal(InviteKey);
        var change = role.RequiredValue.RolesChanged.Should().ContainSingle().Subject;
        change.PublicKey.Should().Equal(OtherKey);
        change.Role.Should().Be(WriteKeyRole.Admin);
    }

    [Fact]
    public void Append_ShouldRevokeKey_WhenKeyRemoved()
    {
        var ledger = new LedgerStream(Guid.NewGuid(), CreatorKey);
        Join(ledger, OtherKey, WriteKeyRole.Writer);

        Append(ledger, CreatorKey, Bytes(0x10, 32), removed: [OtherKey]);
        var result = Append(ledger, OtherKey, Bytes(0x11, 32));

        ledger.HasWriteKey(OtherKey).Should().BeFalse();
        result.Success.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Forbidden);
    }

    [Fact]
    public void Append_ShouldRefuseAddedKeys_FromAWriteKey()
    {
        var ledger = new LedgerStream(Guid.NewGuid(), CreatorKey);

        var result = Append(ledger, CreatorKey, Bytes(0x01, 32), added: [OtherKey]);

        result.Success.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Forbidden);
        ShouldBeUnchanged(ledger, 0, new byte[32], 0, CreatorKey);
    }

    [Fact]
    public void Append_ShouldFailWithNotFound_WhenArchived()
    {
        var ledger = new LedgerStream(Guid.NewGuid(), CreatorKey);
        var archiveHash = Bytes(0x0A, 32);
        Archive(ledger, CreatorKey, archiveHash);

        var result = Append(ledger, CreatorKey, Bytes(0x01, 32));

        result.Success.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
        ShouldBeUnchanged(ledger, 1, archiveHash, 1, CreatorKey);
    }

    [Fact]
    public void Append_ShouldFailWithForbidden_WhenKeyUnknown()
    {
        var ledger = new LedgerStream(Guid.NewGuid(), CreatorKey);

        var result = Append(ledger, UnknownKey, Bytes(0x01, 32));

        result.Success.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Forbidden);
        ShouldBeUnchanged(ledger, 0, new byte[32], 0, CreatorKey);
    }

    [Fact]
    public void Append_ShouldFailWithConflict_WhenExpectedVersionIsStale()
    {
        var ledger = new LedgerStream(Guid.NewGuid(), CreatorKey);
        var headHash = Bytes(0x01, 32);
        Append(ledger, CreatorKey, headHash);

        var result = Append(ledger, CreatorKey, Bytes(0x02, 32), expectedVersion: 0);

        result.Success.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Conflict);
        ShouldBeUnchanged(ledger, 1, headHash, 1, CreatorKey);
    }

    [Fact]
    public void Append_ShouldFailWithConflict_WhenPreviousHashDoesNotMatch()
    {
        var ledger = new LedgerStream(Guid.NewGuid(), CreatorKey);
        var headHash = Bytes(0x01, 32);
        Append(ledger, CreatorKey, headHash);

        var result = Append(ledger, CreatorKey, Bytes(0x02, 32), previousHash: Bytes(0xFF, 32));

        result.Success.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Conflict);
        ShouldBeUnchanged(ledger, 1, headHash, 1, CreatorKey);
    }

    [Fact]
    public void Append_ShouldFailWithValidation_WhenRemovingLastKey()
    {
        var ledger = new LedgerStream(Guid.NewGuid(), CreatorKey);

        var result = Append(ledger, CreatorKey, Bytes(0x01, 32), removed: [CreatorKey]);

        result.Success.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        ShouldBeUnchanged(ledger, 0, new byte[32], 0, CreatorKey);
    }

    public static TheoryData<string> MalformedInputs =>
    [
        "emptyPayload",
        "shortPreviousHash",
        "shortEventHash",
        "shortWriteKey",
        "shortSignature",
        "shortAddedKey",
        "shortInviteKey",
        "shortRemovedKey",
        "shortRoleKey",
    ];

    [Theory]
    [MemberData(nameof(MalformedInputs))]
    public void Append_ShouldThrow_WhenInputIsMalformed(string input)
    {
        var ledger = new LedgerStream(Guid.NewGuid(), CreatorKey);

        var act = () => ledger.Append(
            expectedVersion: 0,
            previousHash: input == "shortPreviousHash" ? Bytes(0x00, 31) : new byte[32],
            eventHash: input == "shortEventHash" ? Bytes(0x01, 31) : Bytes(0x01, 32),
            payload: input == "emptyPayload" ? [] : [1, 2, 3],
            writeKeyPublic: input == "shortWriteKey" ? CreatorKey[..31] : CreatorKey,
            signature: input == "shortSignature" ? Bytes(0x00, 63) : Bytes(0x00, 64),
            keyChanges: new KeyChanges(
                input == "shortAddedKey" ? [OtherKey, Bytes(0xD4, 31)] : [],
                input == "shortInviteKey" ? [Writer(Bytes(0xD4, 31))] : [],
                input == "shortRemovedKey" ? [Bytes(0xD4, 31)] : [],
                input == "shortRoleKey" ? [Writer(Bytes(0xD4, 31))] : []));

        act.Should().Throw<ArgumentException>();
        ShouldBeUnchanged(ledger, 0, new byte[32], 0, CreatorKey);
    }

    [Fact]
    public void Archive_ShouldAppendEventAndArchive_WhenKeyIsValid()
    {
        var ledger = new LedgerStream(Guid.NewGuid(), CreatorKey);
        var archiveHash = Bytes(0x0A, 32);

        var result = Archive(ledger, CreatorKey, archiveHash);

        result.Success.Should().BeTrue();
        result.RequiredValue.Version.Should().Be(1);
        ledger.Archived.Should().BeTrue();
        ledger.Version.Should().Be(1);
        ledger.Hash.Should().Equal(archiveHash);
        ledger.Events.Should().ContainSingle();
    }

    [Fact]
    public void Archive_ShouldFailWithForbidden_WhenKeyUnknown()
    {
        var ledger = new LedgerStream(Guid.NewGuid(), CreatorKey);

        var result = Archive(ledger, UnknownKey, Bytes(0x0A, 32));

        result.Success.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Forbidden);
        ledger.Archived.Should().BeFalse();
        ShouldBeUnchanged(ledger, 0, new byte[32], 0, CreatorKey);
    }

    [Fact]
    public void Archive_ShouldFailWithNotFound_WhenAlreadyArchived()
    {
        var ledger = new LedgerStream(Guid.NewGuid(), CreatorKey);
        Archive(ledger, CreatorKey, Bytes(0x0A, 32));

        var result = Archive(ledger, CreatorKey, Bytes(0x0B, 32));

        result.Success.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
        ledger.Archived.Should().BeTrue();
        ledger.Events.Should().ContainSingle();
    }

    private static readonly byte[] InviteKey = Bytes(0xE5, 32);
    private static readonly byte[] SwappedKey = Bytes(0xF6, 32);
    private static readonly byte[] AdminKey = Bytes(0x9A, 32);
    private static readonly byte[] SecondAdminKey = Bytes(0x9B, 32);
    private static readonly byte[] SecondWriterKey = Bytes(0x8C, 32);

    private static KeyGrant Writer(byte[] key) => new(key, WriteKeyRole.Writer);
    private static KeyGrant Admin(byte[] key) => new(key, WriteKeyRole.Admin);
    private static KeyGrant Owner(byte[] key) => new(key, WriteKeyRole.Owner);

    private static void Join(LedgerStream ledger, byte[] key, WriteKeyRole role)
    {
        var invite = Bytes((byte)(key[0] ^ 0xFF), 32);
        Append(ledger, CreatorKey, Bytes((byte)(ledger.Version + 1), 32), invites: [new KeyGrant(invite, role)]).Success.Should().BeTrue();
        Append(ledger, invite, Bytes((byte)(ledger.Version + 1), 32), added: [key], removed: [invite]).Success.Should().BeTrue();
    }

    private static LedgerStream LedgerWithInvite()
    {
        var ledger = new LedgerStream(Guid.NewGuid(), CreatorKey);
        Append(ledger, CreatorKey, Bytes(0x01, 32), invites: [Writer(InviteKey)]);
        return ledger;
    }

    private static LedgerStream LedgerWithMembers()
    {
        var ledger = new LedgerStream(Guid.NewGuid(), CreatorKey);
        Join(ledger, AdminKey, WriteKeyRole.Admin);
        Join(ledger, SecondAdminKey, WriteKeyRole.Admin);
        Join(ledger, OtherKey, WriteKeyRole.Writer);
        Join(ledger, SecondWriterKey, WriteKeyRole.Writer);
        return ledger;
    }

    private static LedgerWriteKey KeyOf(LedgerStream ledger, byte[] key) =>
        ledger.WriteKeys.Single(k => k.PublicKey.SequenceEqual(key));

    [Fact]
    public void Constructor_ShouldMakeCreatorKeyOwner()
    {
        var ledger = new LedgerStream(Guid.NewGuid(), CreatorKey);

        KeyOf(ledger, CreatorKey).Role.Should().Be(WriteKeyRole.Owner);
        KeyOf(ledger, CreatorKey).IsInvite.Should().BeFalse();
    }

    [Fact]
    public void Append_ShouldStoreInviteKeys_WithTheirRole()
    {
        var ledger = LedgerWithInvite();

        ledger.HasWriteKey(InviteKey).Should().BeTrue();
        KeyOf(ledger, InviteKey).IsInvite.Should().BeTrue();
        KeyOf(ledger, InviteKey).Role.Should().Be(WriteKeyRole.Writer);
    }

    [Fact]
    public void Initialize_ShouldStoreInviteKeys()
    {
        var ledger = LedgerStream.Initialize(Guid.NewGuid(), CreatorKey, [1, 2, 3], Bytes(0x01, 32), Bytes(0x00, 64), [Writer(InviteKey), Admin(OtherKey)]).RequiredValue;

        KeyOf(ledger, InviteKey).IsInvite.Should().BeTrue();
        KeyOf(ledger, InviteKey).Role.Should().Be(WriteKeyRole.Writer);
        KeyOf(ledger, OtherKey).IsInvite.Should().BeTrue();
        KeyOf(ledger, OtherKey).Role.Should().Be(WriteKeyRole.Admin);
    }

    [Theory]
    [InlineData(WriteKeyRole.Writer)]
    [InlineData(WriteKeyRole.Admin)]
    [InlineData(WriteKeyRole.Owner)]
    public void Append_ShouldGiveSwappedKey_TheInvitesRole(WriteKeyRole role)
    {
        var ledger = new LedgerStream(Guid.NewGuid(), CreatorKey);
        Append(ledger, CreatorKey, Bytes(0x01, 32), invites: [new KeyGrant(InviteKey, role)]);

        var result = Append(ledger, InviteKey, Bytes(0x02, 32), added: [SwappedKey], removed: [InviteKey]);

        result.Success.Should().BeTrue();
        ledger.HasWriteKey(InviteKey).Should().BeFalse();
        KeyOf(ledger, SwappedKey).IsInvite.Should().BeFalse();
        KeyOf(ledger, SwappedKey).Role.Should().Be(role);
    }

    [Fact]
    public void Append_ShouldRefuseInviteKey_AfterItSwapped()
    {
        var ledger = LedgerWithInvite();
        Append(ledger, InviteKey, Bytes(0x02, 32), added: [SwappedKey], removed: [InviteKey]);

        var result = Append(ledger, InviteKey, Bytes(0x03, 32), added: [OtherKey], removed: [InviteKey]);

        result.Success.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Forbidden);
    }

    public static TheoryData<string> InviteViolations =>
    [
        "plainAppend",
        "removeOtherKey",
        "addTwoKeys",
        "addInvite",
        "addExistingKey",
        "removeSelfOnly",
        "changeRole",
    ];

    [Theory]
    [MemberData(nameof(InviteViolations))]
    public void Append_ShouldRefuseInviteKey_WhenNotASelfSwap(string violation)
    {
        var ledger = LedgerWithInvite();

        var result = violation switch
        {
            "plainAppend" => Append(ledger, InviteKey, Bytes(0x02, 32)),
            "removeOtherKey" => Append(ledger, InviteKey, Bytes(0x02, 32), added: [SwappedKey], removed: [CreatorKey]),
            "addTwoKeys" => Append(ledger, InviteKey, Bytes(0x02, 32), added: [SwappedKey, OtherKey], removed: [InviteKey]),
            "addInvite" => Append(ledger, InviteKey, Bytes(0x02, 32), added: [SwappedKey], removed: [InviteKey], invites: [Writer(OtherKey)]),
            "addExistingKey" => Append(ledger, InviteKey, Bytes(0x02, 32), added: [CreatorKey], removed: [InviteKey]),
            "removeSelfOnly" => Append(ledger, InviteKey, Bytes(0x02, 32), removed: [InviteKey]),
            "changeRole" => Append(ledger, InviteKey, Bytes(0x02, 32), added: [SwappedKey], removed: [InviteKey], roles: [Owner(SwappedKey)]),
            _ => throw new ArgumentOutOfRangeException(nameof(violation)),
        };

        result.Success.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Forbidden);
        ShouldBeUnchanged(ledger, 1, Bytes(0x01, 32), 1, CreatorKey, InviteKey);
    }

    [Fact]
    public void Archive_ShouldRefuseInviteKey()
    {
        var ledger = LedgerWithInvite();

        var result = Archive(ledger, InviteKey, Bytes(0x0A, 32));

        result.Success.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Forbidden);
        ledger.Archived.Should().BeFalse();
    }

    [Fact]
    public void Append_ShouldFailWithValidation_WhenOnlyInviteKeysWouldRemain()
    {
        var ledger = LedgerWithInvite();

        var result = Append(ledger, CreatorKey, Bytes(0x02, 32), removed: [CreatorKey]);

        result.Success.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
    }

    [Theory]
    [InlineData(WriteKeyRole.Writer, WriteKeyRole.Writer, true)]
    [InlineData(WriteKeyRole.Writer, WriteKeyRole.Admin, false)]
    [InlineData(WriteKeyRole.Admin, WriteKeyRole.Admin, true)]
    [InlineData(WriteKeyRole.Admin, WriteKeyRole.Owner, false)]
    [InlineData(WriteKeyRole.Owner, WriteKeyRole.Owner, true)]
    public void Append_ShouldOnlyAllowInvites_UpToTheAuthorsRole(WriteKeyRole author, WriteKeyRole invite, bool allowed)
    {
        var ledger = LedgerWithMembers();
        var authorKey = author switch
        {
            WriteKeyRole.Writer => OtherKey,
            WriteKeyRole.Admin => AdminKey,
            _ => CreatorKey,
        };

        var result = Append(ledger, authorKey, Bytes(0x20, 32), invites: [new KeyGrant(InviteKey, invite)]);

        result.Success.Should().Be(allowed);
        ledger.HasWriteKey(InviteKey).Should().Be(allowed);
        if (!allowed)
            result.Error!.Type.Should().Be(ErrorType.Forbidden);
    }

    public static TheoryData<string, string, bool> Removals => new()
    {
        { "writer", "writer", true },
        { "writer", "otherWriter", false },
        { "writer", "admin", false },
        { "writer", "owner", false },
        { "admin", "admin", true },
        { "admin", "writer", true },
        { "admin", "invite", true },
        { "admin", "otherAdmin", false },
        { "admin", "owner", false },
        { "owner", "otherAdmin", true },
        { "owner", "writer", true },
        { "owner", "owner", true },
    };

    [Theory]
    [MemberData(nameof(Removals))]
    public void Append_ShouldOnlyAllowRemovals_ThatTheAuthorsRolePermits(string author, string target, bool allowed)
    {
        var ledger = LedgerWithMembers();
        Append(ledger, CreatorKey, Bytes(0x20, 32), invites: [Writer(InviteKey)]);
        byte[] KeyFor(string name) => name switch
        {
            "writer" => OtherKey,
            "otherWriter" => SecondWriterKey,
            "admin" => AdminKey,
            "otherAdmin" => SecondAdminKey,
            "invite" => InviteKey,
            _ => CreatorKey,
        };

        var result = Append(ledger, KeyFor(author), Bytes(0x21, 32), removed: [KeyFor(target)]);

        result.Success.Should().Be(allowed);
        ledger.HasWriteKey(KeyFor(target)).Should().Be(!allowed);
        if (!allowed)
            result.Error!.Type.Should().Be(ErrorType.Forbidden);
    }

    [Fact]
    public void Append_ShouldChangeRoles_WhenSignedByOwner()
    {
        var ledger = LedgerWithMembers();

        var result = Append(ledger, CreatorKey, Bytes(0x20, 32), roles: [Admin(OtherKey), Writer(AdminKey)]);

        result.Success.Should().BeTrue();
        KeyOf(ledger, OtherKey).Role.Should().Be(WriteKeyRole.Admin);
        KeyOf(ledger, AdminKey).Role.Should().Be(WriteKeyRole.Writer);
    }

    [Fact]
    public void Append_ShouldRefuseRoleChanges_FromAdmin()
    {
        var ledger = LedgerWithMembers();

        var result = Append(ledger, AdminKey, Bytes(0x20, 32), roles: [Admin(OtherKey)]);

        result.Success.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Forbidden);
        KeyOf(ledger, OtherKey).Role.Should().Be(WriteKeyRole.Writer);
    }

    [Fact]
    public void Append_ShouldRefuseRoleChanges_ForKeysNotInTheLedger()
    {
        var ledger = LedgerWithMembers();

        var result = Append(ledger, CreatorKey, Bytes(0x20, 32), roles: [Admin(UnknownKey)]);

        result.Success.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
    }
}
