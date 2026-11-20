using FluentAssertions;
using Ledger.Application.Abstractions;
using Ledger.Application.Commands.Append;
using Ledger.Application.Interfaces;
using Ledger.Domain.Aggregates;
using Ledger.Domain.Common;
using Ledger.Domain.Enums;
using Ledger.Infrastructure.Persistence.Context;
using Ledger.Test.Integration.Setup;
using Ledger.Test.Integration.Setup.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSec.Cryptography;

namespace Ledger.Test.Integration.LedgerTest.CommandTest.AppendTest;

public class AppendLedgerEventCommandHandlerTest : IntegrationTestBase
{
    private readonly AppendLedgerEventCommandHandler _sut;
    private readonly Key _creator = LedgerSigner.NewKey();

    public AppendLedgerEventCommandHandlerTest(IntegrationTestFactory<Program, LedgerDbContext> factory) : base(factory)
    {
        var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ILedgerRepository>();
        var signatureHelper = scope.ServiceProvider.GetRequiredService<ISignatureHelper>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        _sut = new AppendLedgerEventCommandHandler(repository, signatureHelper, unitOfWork);
    }

    [Fact]
    public async Task Handle_Given_Valid_Command_Should_Append_And_Chain_To_Previous_Event()
    {
        // Arrange
        var ledger = await LedgerFactory.SeedAsync(Db, _creator);
        var command = LedgerCommands.Append(ledger).SignedBy(_creator);

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();

        var stored = await Db.LedgerStream.AsNoTracking().Include(l => l.Events).FirstAsync(l => l.Id == ledger.Id);

        stored.Version.Should().Be(2);
        stored.Hash.Should().Equal(LedgerSigner.Hash(ledger.Id, 2, ledger.Hash, command.Payload));

        var appended = stored.Events.Single(e => e.Version == 2);
        appended.PreviousHash.Should().Equal(ledger.Hash);
        appended.Payload.Should().Equal(command.Payload);
    }

    [Fact]
    public async Task Handle_Given_Unknown_Ledger_Should_Return_NotFound()
    {
        // Arrange
        var ledger = await LedgerFactory.SeedAsync(Db, _creator);
        var command = LedgerCommands.Append(ledger).SignedBy(_creator) with { LedgerId = Guid.NewGuid() };

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public async Task Handle_Given_Signature_From_Unknown_Key_Should_Return_Forbidden()
    {
        // Arrange
        var ledger = await LedgerFactory.SeedAsync(Db, _creator);
        var command = LedgerCommands.Append(ledger).SignedBy(LedgerSigner.NewKey());

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Forbidden);
        Db.LedgerEvent.AsNoTracking().Where(e => e.StreamId == ledger.Id).Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_Given_Payload_Changed_After_Signing_Should_Return_Forbidden()
    {
        // Arrange
        var ledger = await LedgerFactory.SeedAsync(Db, _creator);
        var command = LedgerCommands.Append(ledger).SignedBy(_creator) with { Payload = [9, 9, 9] };

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Forbidden);
    }

    [Fact]
    public async Task Handle_Given_Signature_Over_Expected_Version_Should_Return_Forbidden()
    {
        // Arrange
        var ledger = await LedgerFactory.SeedAsync(Db, _creator);
        var command = LedgerCommands.Append(ledger) with
        {
            WriteKeyPublic = LedgerSigner.PublicKeyOf(_creator),
            Signature = LedgerSigner.Sign(_creator, ledger.Id, ledger.Version, ledger.Hash, [4, 5, 6]),
        };

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Forbidden);
    }

    [Fact]
    public async Task Handle_Given_Stale_Expected_Version_Should_Return_Conflict()
    {
        // Arrange
        var ledger = await LedgerFactory.SeedAsync(Db, _creator);
        var command = (LedgerCommands.Append(ledger) with
        {
            ExpectedVersion = 0,
            PreviousHash = LedgerFactory.GenesisHash,
        }).SignedBy(_creator);

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Conflict);
        Db.LedgerEvent.AsNoTracking().Where(e => e.StreamId == ledger.Id).Should().ContainSingle();
    }

    private static KeyGrant Grant(Key key, WriteKeyRole role) => new(LedgerSigner.PublicKeyOf(key), role);

    private async Task<LedgerStream> JoinAsync(LedgerStream ledger, Key member, WriteKeyRole role)
    {
        var invite = LedgerSigner.NewKey();

        var granted = await _sut.Handle((LedgerCommands.Append(ledger) with
        {
            KeyChanges = new KeyChanges([], [Grant(invite, role)], [], []),
        }).SignedBy(_creator), CancellationToken.None);
        granted.Success.Should().BeTrue();

        var afterInvite = await LedgerFactory.ReloadAsync(Db, ledger.Id);
        var swapped = await _sut.Handle((LedgerCommands.Append(afterInvite) with
        {
            KeyChanges = new KeyChanges([LedgerSigner.PublicKeyOf(member)], [], [LedgerSigner.PublicKeyOf(invite)], []),
        }).SignedBy(invite), CancellationToken.None);
        swapped.Success.Should().BeTrue();

        return await LedgerFactory.ReloadAsync(Db, ledger.Id);
    }

    [Fact]
    public async Task Handle_Given_Write_Key_Adding_A_Key_Directly_Should_Return_Forbidden()
    {
        // Arrange
        var ledger = await LedgerFactory.SeedAsync(Db, _creator);
        var command = (LedgerCommands.Append(ledger) with
        {
            KeyChanges = new KeyChanges([LedgerSigner.PublicKeyOf(LedgerSigner.NewKey())], [], [], []),
        }).SignedBy(_creator);

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Forbidden);
        Db.LedgerEvent.AsNoTracking().Where(e => e.StreamId == ledger.Id).Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_Given_Key_Removed_Should_Reject_That_Key_Afterwards()
    {
        // Arrange
        var member = LedgerSigner.NewKey();
        var ledger = await JoinAsync(await LedgerFactory.SeedAsync(Db, _creator), member, WriteKeyRole.Writer);

        // Act
        var removed = await _sut.Handle((LedgerCommands.Append(ledger, [8, 8, 8]) with
        {
            KeyChanges = new KeyChanges([], [], [LedgerSigner.PublicKeyOf(member)], []),
        }).SignedBy(_creator), CancellationToken.None);

        var afterRevoke = await LedgerFactory.ReloadAsync(Db, ledger.Id);
        var result = await _sut.Handle(LedgerCommands.Append(afterRevoke, [9, 9, 9]).SignedBy(member), CancellationToken.None);

        // Assert
        removed.Success.Should().BeTrue();
        afterRevoke.WriteKeys.Should().ContainSingle();
        result.Success.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Forbidden);
    }

    [Fact]
    public async Task Handle_Given_Writer_Removing_Another_Key_Should_Return_Forbidden()
    {
        // Arrange
        var writer = LedgerSigner.NewKey();
        var other = LedgerSigner.NewKey();
        var ledger = await LedgerFactory.SeedAsync(Db, _creator);
        ledger = await JoinAsync(ledger, writer, WriteKeyRole.Writer);
        ledger = await JoinAsync(ledger, other, WriteKeyRole.Writer);

        var command = (LedgerCommands.Append(ledger) with
        {
            KeyChanges = new KeyChanges([], [], [LedgerSigner.PublicKeyOf(other), LedgerSigner.PublicKeyOf(_creator)], []),
        }).SignedBy(writer);

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Forbidden);
        var stored = await LedgerFactory.ReloadAsync(Db, ledger.Id);
        stored.WriteKeys.Should().HaveCount(3);
    }

    [Fact]
    public async Task Handle_Given_Writer_Removing_Its_Own_Key_Should_Succeed()
    {
        // Arrange
        var writer = LedgerSigner.NewKey();
        var ledger = await JoinAsync(await LedgerFactory.SeedAsync(Db, _creator), writer, WriteKeyRole.Writer);

        var command = (LedgerCommands.Append(ledger) with
        {
            KeyChanges = new KeyChanges([], [], [LedgerSigner.PublicKeyOf(writer)], []),
        }).SignedBy(writer);

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        var stored = await LedgerFactory.ReloadAsync(Db, ledger.Id);
        stored.HasWriteKey(LedgerSigner.PublicKeyOf(writer)).Should().BeFalse();
    }

    [Fact]
    public async Task Handle_Given_Owner_Role_Change_Should_Persist_It()
    {
        // Arrange
        var member = LedgerSigner.NewKey();
        var ledger = await JoinAsync(await LedgerFactory.SeedAsync(Db, _creator), member, WriteKeyRole.Writer);

        var command = (LedgerCommands.Append(ledger) with
        {
            KeyChanges = new KeyChanges([], [], [], [Grant(member, WriteKeyRole.Admin)]),
        }).SignedBy(_creator);

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        var evt = await Db.LedgerEvent.AsNoTracking().SingleAsync(e => e.StreamId == ledger.Id && e.Version == command.ExpectedVersion + 1);
        var change = evt.RolesChanged.Should().ContainSingle().Subject;
        change.PublicKey.Should().Equal(LedgerSigner.PublicKeyOf(member));
        change.Role.Should().Be(WriteKeyRole.Admin);
        var stored = await LedgerFactory.ReloadAsync(Db, ledger.Id);
        stored.WriteKeys.Single(k => k.PublicKey.SequenceEqual(LedgerSigner.PublicKeyOf(member))).Role.Should().Be(WriteKeyRole.Admin);
    }

    [Fact]
    public async Task Handle_Given_Admin_Role_Change_Should_Return_Forbidden()
    {
        // Arrange
        var admin = LedgerSigner.NewKey();
        var member = LedgerSigner.NewKey();
        var ledger = await LedgerFactory.SeedAsync(Db, _creator);
        ledger = await JoinAsync(ledger, admin, WriteKeyRole.Admin);
        ledger = await JoinAsync(ledger, member, WriteKeyRole.Writer);

        var command = (LedgerCommands.Append(ledger) with
        {
            KeyChanges = new KeyChanges([], [], [], [Grant(member, WriteKeyRole.Admin)]),
        }).SignedBy(admin);

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Forbidden);
    }

    [Fact]
    public async Task Handle_Given_Invite_Above_The_Authors_Role_Should_Return_Forbidden()
    {
        // Arrange
        var writer = LedgerSigner.NewKey();
        var ledger = await JoinAsync(await LedgerFactory.SeedAsync(Db, _creator), writer, WriteKeyRole.Writer);

        var command = (LedgerCommands.Append(ledger) with
        {
            KeyChanges = new KeyChanges([], [Grant(LedgerSigner.NewKey(), WriteKeyRole.Admin)], [], []),
        }).SignedBy(writer);

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Forbidden);
    }

    [Fact]
    public async Task Handle_Given_Invite_Self_Swap_Should_Replace_The_Invite_With_A_Key_Of_Its_Role()
    {
        // Arrange
        var invite = LedgerSigner.NewKey();
        var own = LedgerSigner.PublicKeyOf(LedgerSigner.NewKey());
        var ledger = await LedgerFactory.SeedAsync(Db, _creator, inviteKeys: [Grant(invite, WriteKeyRole.Admin)]);

        var swap = (LedgerCommands.Append(ledger) with
        {
            KeyChanges = new KeyChanges([own], [], [LedgerSigner.PublicKeyOf(invite)], []),
        }).SignedBy(invite);

        // Act
        var result = await _sut.Handle(swap, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();

        var stored = await LedgerFactory.ReloadAsync(Db, ledger.Id);
        stored.HasWriteKey(LedgerSigner.PublicKeyOf(invite)).Should().BeFalse();
        var key = stored.WriteKeys.Single(k => k.PublicKey.SequenceEqual(own));
        key.Role.Should().Be(WriteKeyRole.Admin);
        key.IsInvite.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_Given_Invite_Key_Plain_Append_Should_Return_Forbidden()
    {
        // Arrange
        var invite = LedgerSigner.NewKey();
        var ledger = await LedgerFactory.SeedAsync(Db, _creator, inviteKeys: [Grant(invite, WriteKeyRole.Writer)]);

        // Act
        var result = await _sut.Handle(LedgerCommands.Append(ledger).SignedBy(invite), CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Forbidden);
        Db.LedgerEvent.AsNoTracking().Where(e => e.StreamId == ledger.Id).Should().HaveCount(1);
    }

    [Fact]
    public async Task Handle_Given_Invites_Persisted_On_The_Event_And_Stored_As_Invite_Keys()
    {
        // Arrange
        var ledger = await LedgerFactory.SeedAsync(Db, _creator);
        var invite = LedgerSigner.PublicKeyOf(LedgerSigner.NewKey());
        var grant = new KeyGrant(invite, WriteKeyRole.Writer);
        var command = (LedgerCommands.Append(ledger) with { KeyChanges = new KeyChanges([], [grant], [], []) }).SignedBy(_creator);

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();

        var stored = await Db.LedgerEvent.AsNoTracking().SingleAsync(e => e.StreamId == ledger.Id && e.Version == 2);
        var storedInvite = stored.InvitesAdded.Should().ContainSingle().Subject;
        storedInvite.PublicKey.Should().Equal(invite);
        storedInvite.Role.Should().Be(WriteKeyRole.Writer);
        stored.KeysAdded.Should().BeEmpty();

        var reloaded = await LedgerFactory.ReloadAsync(Db, ledger.Id);
        var key = reloaded.WriteKeys.Single(k => k.PublicKey.SequenceEqual(invite));
        key.IsInvite.Should().BeTrue();
        key.Role.Should().Be(WriteKeyRole.Writer);
        reloaded.Hash.Should().Equal(LedgerSigner.Hash(ledger.Id, 2, ledger.Hash, command.Payload, command.KeyChanges));
    }

    [Fact]
    public async Task Handle_Given_Invites_Changed_After_Signing_Should_Return_Forbidden()
    {
        // Arrange
        var ledger = await LedgerFactory.SeedAsync(Db, _creator);
        var signed = (LedgerCommands.Append(ledger) with
        {
            KeyChanges = new KeyChanges([], [Grant(LedgerSigner.NewKey(), WriteKeyRole.Writer)], [], []),
        }).SignedBy(_creator);
        var tampered = signed with
        {
            KeyChanges = signed.KeyChanges with
            {
                InvitesAdded = [.. signed.KeyChanges.InvitesAdded, Grant(LedgerSigner.NewKey(), WriteKeyRole.Writer)],
            },
        };

        // Act
        var result = await _sut.Handle(tampered, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Forbidden);
    }
}
