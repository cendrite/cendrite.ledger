using System.Buffers.Binary;
using System.Security.Cryptography;
using FluentAssertions;
using Ledger.Application.Common;
using Ledger.Application.Services;
using Ledger.Domain.Common;
using Ledger.Domain.Enums;
using NSec.Cryptography;

namespace Ledger.Test.Unit.ServiceTest;

public class SignatureHelperTest
{
    private static readonly SignatureAlgorithm Ed25519 = SignatureAlgorithm.Ed25519;

    private readonly SignatureHelper _sut = new();
    private readonly Guid _ledgerId = Guid.NewGuid();
    private readonly byte[] _previousHash = Enumerable.Repeat((byte)0x07, 32).ToArray();
    private readonly byte[] _payload = [1, 2, 3];

    private static Key NewKey() =>
        Key.Create(Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

    private static byte[] Public(Key key) => key.PublicKey.Export(KeyBlobFormat.RawPublicKey);

    private static byte[] Bytes(byte value, int length) => [.. Enumerable.Repeat(value, length)];

    private static KeyGrant[] Writers(params byte[][] keys) => [.. keys.Select(k => new KeyGrant(k, WriteKeyRole.Writer))];

    private byte[] Sign(Key key, byte[]? payload = null, int version = 1, byte[][]? added = null, KeyGrant[]? invites = null, byte[][]? removed = null, KeyGrant[]? roles = null) =>
        Ed25519.Sign(key, ExpectedSigningInput(_ledgerId, version, _previousHash, payload ?? _payload, added ?? [], invites ?? [], removed ?? [], roles ?? []));

    // Built independently of the implementation: if the layout changes, these tests fail instead of following along.
    private static byte[] ExpectedSigningInput(
        Guid ledgerId, int version, byte[] previousHash, byte[] payload, byte[][] added, KeyGrant[] invites, byte[][] removed, KeyGrant[] roles)
    {
        var versionBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(versionBytes, version);

        using var input = new MemoryStream();
        input.Write("ledger-v1"u8);
        input.Write(ledgerId.ToByteArray(bigEndian: true));
        input.Write(versionBytes);
        input.Write(previousHash);
        WriteKeys(input, added);
        WriteGrants(input, invites);
        WriteKeys(input, removed);
        WriteGrants(input, roles);
        input.Write(payload);

        return input.ToArray();
    }

    private static void WriteKeys(Stream input, byte[][] keys)
    {
        var count = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(count, keys.Length);
        input.Write(count);

        foreach (var key in keys.OrderBy(Convert.ToHexString, StringComparer.Ordinal))
            input.Write(key);
    }

    private static void WriteGrants(Stream input, KeyGrant[] grants)
    {
        var count = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(count, grants.Length);
        input.Write(count);

        foreach (var grant in grants.OrderBy(g => Convert.ToHexString(g.PublicKey), StringComparer.Ordinal))
        {
            input.Write(grant.PublicKey);
            input.WriteByte((byte)grant.Role);
        }
    }

    [Fact]
    public void GenerateHash_ShouldMatchProtocolLayout()
    {
        var expected = SHA256.HashData(ExpectedSigningInput(_ledgerId, 1, _previousHash, _payload, [], [], [], []));

        var hash = _sut.GenerateHash(_ledgerId, 1, _previousHash, _payload, new KeyChanges([], [], [], []));

        hash.Should().HaveCount(32);
        hash.Should().Equal(expected);
    }

    [Fact]
    public void GenerateHash_ShouldBeDeterministic()
    {
        var first = _sut.GenerateHash(_ledgerId, 1, _previousHash, _payload, new KeyChanges([], [], [], []));
        var second = _sut.GenerateHash(_ledgerId, 1, _previousHash, _payload, new KeyChanges([], [], [], []));

        first.Should().Equal(second);
    }

    [Fact]
    public void GenerateHash_ShouldDifferPerLedger()
    {
        var first = _sut.GenerateHash(_ledgerId, 1, _previousHash, _payload, new KeyChanges([], [], [], []));
        var second = _sut.GenerateHash(Guid.NewGuid(), 1, _previousHash, _payload, new KeyChanges([], [], [], []));

        first.Should().NotEqual(second);
    }

    [Fact]
    public void GenerateHash_ShouldDifferPerVersion()
    {
        var first = _sut.GenerateHash(_ledgerId, 1, _previousHash, _payload, new KeyChanges([], [], [], []));
        var second = _sut.GenerateHash(_ledgerId, 2, _previousHash, _payload, new KeyChanges([], [], [], []));

        first.Should().NotEqual(second);
    }

    [Fact]
    public void GenerateHash_ShouldDifferPerPreviousHash()
    {
        var first = _sut.GenerateHash(_ledgerId, 1, _previousHash, _payload, new KeyChanges([], [], [], []));
        var second = _sut.GenerateHash(_ledgerId, 1, Bytes(0x08, 32), _payload, new KeyChanges([], [], [], []));

        first.Should().NotEqual(second);
    }

    [Fact]
    public void GenerateHash_ShouldIgnoreKeyOrder()
    {
        byte[] a = Bytes(0x01, 32);
        byte[] b = Bytes(0x02, 32);

        var first = _sut.GenerateHash(_ledgerId, 1, _previousHash, _payload, new KeyChanges([a, b], [], [], []));
        var second = _sut.GenerateHash(_ledgerId, 1, _previousHash, _payload, new KeyChanges([b, a], [], [], []));

        first.Should().Equal(second);
    }

    [Fact]
    public void GenerateHash_ShouldDistinguishAddedFromRemoved()
    {
        var key = Bytes(0x01, 32);

        var added = _sut.GenerateHash(_ledgerId, 1, _previousHash, _payload, new KeyChanges([key], [], [], []));
        var removed = _sut.GenerateHash(_ledgerId, 1, _previousHash, _payload, new KeyChanges([], [], [key], []));

        added.Should().NotEqual(removed);
    }

    [Fact]
    public void GenerateHash_ShouldDistinguishInvitesFromAddedAndRemoved()
    {
        var key = Bytes(0x01, 32);

        var added = _sut.GenerateHash(_ledgerId, 1, _previousHash, _payload, new KeyChanges([key], [], [], []));
        var invited = _sut.GenerateHash(_ledgerId, 1, _previousHash, _payload, new KeyChanges([], Writers(key), [], []));
        var removed = _sut.GenerateHash(_ledgerId, 1, _previousHash, _payload, new KeyChanges([], [], [key], []));

        invited.Should().NotEqual(added);
        invited.Should().NotEqual(removed);
    }

    [Fact]
    public void GenerateHash_ShouldDistinguishKeyFromPayload()
    {
        var key = Bytes(0x01, 32);

        var asKey = _sut.GenerateHash(_ledgerId, 1, _previousHash, _payload, new KeyChanges([key], [], [], []));
        var asPayload = _sut.GenerateHash(_ledgerId, 1, _previousHash, [.. key, .. _payload], new KeyChanges([], [], [], []));

        asKey.Should().NotEqual(asPayload);
    }

    [Fact]
    public void IsValidSignature_ShouldReturnTrue_WhenSignedByThatKey()
    {
        var key = NewKey();

        var valid = _sut.IsValidSignature(_ledgerId, 1, _previousHash, _payload, Public(key), Sign(key), new KeyChanges([], [], [], []));

        valid.Should().BeTrue();
    }

    [Fact]
    public void IsValidSignature_ShouldReturnTrue_WhenKeysAreListedInAnotherOrder()
    {
        var key = NewKey();
        byte[] a = Bytes(0x01, 32);
        byte[] b = Bytes(0x02, 32);

        var valid = _sut.IsValidSignature(_ledgerId, 1, _previousHash, _payload, Public(key),
            Sign(key, added: [a, b]), new KeyChanges([b, a], [], [], []));

        valid.Should().BeTrue();
    }

    [Fact]
    public void IsValidSignature_ShouldReturnFalse_WhenSignedByAnotherKey()
    {
        var key = NewKey();
        var impostor = NewKey();

        var valid = _sut.IsValidSignature(_ledgerId, 1, _previousHash, _payload, Public(key), Sign(impostor), new KeyChanges([], [], [], []));

        valid.Should().BeFalse();
    }

    [Fact]
    public void IsValidSignature_ShouldReturnFalse_WhenPayloadChanged()
    {
        var key = NewKey();

        var valid = _sut.IsValidSignature(_ledgerId, 1, _previousHash, [9, 9, 9], Public(key), Sign(key), new KeyChanges([], [], [], []));

        valid.Should().BeFalse();
    }

    [Fact]
    public void IsValidSignature_ShouldReturnFalse_WhenVersionChanged()
    {
        var key = NewKey();

        var valid = _sut.IsValidSignature(_ledgerId, 2, _previousHash, _payload, Public(key), Sign(key), new KeyChanges([], [], [], []));

        valid.Should().BeFalse();
    }

    [Fact]
    public void IsValidSignature_ShouldReturnFalse_WhenKeyMutationChanged()
    {
        var key = NewKey();
        var signedFor = Bytes(0x01, 32);
        var swapped = Bytes(0x02, 32);

        var valid = _sut.IsValidSignature(_ledgerId, 1, _previousHash, _payload, Public(key),
            Sign(key, added: [signedFor]), new KeyChanges([swapped], [], [], []));

        valid.Should().BeFalse();
    }

    [Fact]
    public void IsValidSignature_ShouldReturnFalse_WhenInvitesChanged()
    {
        var key = NewKey();
        var signedFor = Bytes(0x01, 32);
        var injected = Bytes(0x02, 32);

        var valid = _sut.IsValidSignature(_ledgerId, 1, _previousHash, _payload, Public(key),
            Sign(key, invites: Writers(signedFor)), new KeyChanges([], Writers(signedFor, injected), [], []));

        valid.Should().BeFalse();
    }

    [Fact]
    public void IsValidSignature_ShouldReturnTrue_WhenInvitesAreSigned()
    {
        var key = NewKey();
        byte[] a = Bytes(0x01, 32);
        byte[] b = Bytes(0x02, 32);

        var valid = _sut.IsValidSignature(_ledgerId, 1, _previousHash, _payload, Public(key),
            Sign(key, invites: Writers(a, b)), new KeyChanges([], Writers(b, a), [], []));

        valid.Should().BeTrue();
    }

    [Fact]
    public void GenerateHash_ShouldMatchProtocolLayout_WithGrants()
    {
        var invites = new[] { new KeyGrant(Bytes(0x02, 32), WriteKeyRole.Admin), new KeyGrant(Bytes(0x01, 32), WriteKeyRole.Writer) };
        var roles = new[] { new KeyGrant(Bytes(0x03, 32), WriteKeyRole.Owner) };
        var expected = SHA256.HashData(ExpectedSigningInput(_ledgerId, 1, _previousHash, _payload, [], invites, [], roles));

        var hash = _sut.GenerateHash(_ledgerId, 1, _previousHash, _payload, new KeyChanges([], invites, [], roles));

        hash.Should().Equal(expected);
    }

    [Fact]
    public void GenerateHash_ShouldDistinguishInvitesFromRoleChanges()
    {
        var grant = new KeyGrant(Bytes(0x01, 32), WriteKeyRole.Admin);

        var invited = _sut.GenerateHash(_ledgerId, 1, _previousHash, _payload, new KeyChanges([], [grant], [], []));
        var changed = _sut.GenerateHash(_ledgerId, 1, _previousHash, _payload, new KeyChanges([], [], [], [grant]));

        invited.Should().NotEqual(changed);
    }

    [Fact]
    public void IsValidSignature_ShouldReturnFalse_WhenInviteRoleChanged()
    {
        var key = NewKey();
        var invite = Bytes(0x01, 32);

        var valid = _sut.IsValidSignature(_ledgerId, 1, _previousHash, _payload, Public(key),
            Sign(key, invites: Writers(invite)), new KeyChanges([], [new KeyGrant(invite, WriteKeyRole.Owner)], [], []));

        valid.Should().BeFalse();
    }

    [Fact]
    public void IsValidSignature_ShouldReturnFalse_WhenRoleChangeInjected()
    {
        var key = NewKey();

        var valid = _sut.IsValidSignature(_ledgerId, 1, _previousHash, _payload, Public(key),
            Sign(key), new KeyChanges([], [], [], [new KeyGrant(Bytes(0x01, 32), WriteKeyRole.Owner)]));

        valid.Should().BeFalse();
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(0)]
    public void IsValidSignature_ShouldReturnFalse_WhenPublicKeyIsMalformed(int length)
    {
        var key = NewKey();

        var valid = _sut.IsValidSignature(_ledgerId, 1, _previousHash, _payload, Bytes(0xFF, length), Sign(key), new KeyChanges([], [], [], []));

        valid.Should().BeFalse();
    }

    [Theory]
    [InlineData(63)]
    [InlineData(65)]
    [InlineData(0)]
    public void IsValidSignature_ShouldReturnFalse_WhenSignatureIsMalformed(int length)
    {
        var key = NewKey();

        var valid = _sut.IsValidSignature(_ledgerId, 1, _previousHash, _payload, Public(key), Bytes(0xFF, length), new KeyChanges([], [], [], []));

        valid.Should().BeFalse();
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static byte[] ExpectedReadSigningInput(Guid ledgerId, long timestamp)
    {
        var timestampBytes = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(timestampBytes, timestamp);

        using var input = new MemoryStream();
        input.Write("ledger-read-v1"u8);
        input.Write(ledgerId.ToByteArray(bigEndian: true));
        input.Write(timestampBytes);

        return input.ToArray();
    }

    [Fact]
    public void IsValidReadSignature_Given_Fresh_Signature_Should_Be_Valid()
    {
        var key = NewKey();
        var now = Now();

        var valid = _sut.IsValidReadSignature(_ledgerId, now, Public(key), Ed25519.Sign(key, ExpectedReadSigningInput(_ledgerId, now)));

        valid.Should().BeTrue();
    }

    [Fact]
    public void IsValidReadSignature_Given_Signature_For_Another_Ledger_Should_Be_Invalid()
    {
        var key = NewKey();
        var now = Now();

        var valid = _sut.IsValidReadSignature(_ledgerId, now, Public(key), Ed25519.Sign(key, ExpectedReadSigningInput(Guid.NewGuid(), now)));

        valid.Should().BeFalse();
    }

    [Fact]
    public void IsValidReadSignature_Given_Signature_From_Another_Key_Should_Be_Invalid()
    {
        var now = Now();

        var valid = _sut.IsValidReadSignature(_ledgerId, now, Public(NewKey()), Ed25519.Sign(NewKey(), ExpectedReadSigningInput(_ledgerId, now)));

        valid.Should().BeFalse();
    }

    [Fact]
    public void IsValidReadSignature_Given_Append_Signature_Should_Be_Invalid()
    {
        var key = NewKey();
        var now = Now();

        var valid = _sut.IsValidReadSignature(_ledgerId, now, Public(key), Sign(key));

        valid.Should().BeFalse();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void IsValidReadSignature_Given_Timestamp_Outside_Window_Should_Be_Invalid(int direction)
    {
        var key = NewKey();
        var timestamp = Now() + direction * (LedgerQueryLimits.ReadSignatureMaxAgeSeconds + 5);

        var valid = _sut.IsValidReadSignature(_ledgerId, timestamp, Public(key), Ed25519.Sign(key, ExpectedReadSigningInput(_ledgerId, timestamp)));

        valid.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void IsValidReadSignature_Given_Malformed_Key_Should_Be_Invalid(int length)
    {
        var key = NewKey();
        var now = Now();

        var valid = _sut.IsValidReadSignature(_ledgerId, now, Bytes(0x01, length), Ed25519.Sign(key, ExpectedReadSigningInput(_ledgerId, now)));

        valid.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(63)]
    public void IsValidReadSignature_Given_Malformed_Signature_Should_Be_Invalid(int length)
    {
        var key = NewKey();

        var valid = _sut.IsValidReadSignature(_ledgerId, Now(), Public(key), Bytes(0xFF, length));

        valid.Should().BeFalse();
    }
}
