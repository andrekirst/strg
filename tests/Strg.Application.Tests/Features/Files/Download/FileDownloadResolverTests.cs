using FluentAssertions;
using NSubstitute;
using Strg.Application.Features.Files.Download;
using Strg.Core.Domain;
using Strg.Core.Storage;
using Xunit;

namespace Strg.Application.Tests.Features.Files.Download;

/// <summary>
/// Pins every branch of <see cref="FileDownloadResolver.ResolveAsync"/> at the
/// data-orchestration boundary. Each test stubs the repositories + storage abstractions and
/// asserts the typed <see cref="DownloadFailure"/> case (or the populated
/// <see cref="FileDownloadResult"/> on success). Audit emission is NOT exercised here — that
/// lives in <c>DownloadFileHandler</c>, which is covered by integration tests asserting the
/// audit row exists.
/// </summary>
public sealed class FileDownloadResolverTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly IFileRepository _fileRepo = Substitute.For<IFileRepository>();
    private readonly IDriveRepository _driveRepo = Substitute.For<IDriveRepository>();
    private readonly IFileVersionRepository _versionRepo = Substitute.For<IFileVersionRepository>();
    private readonly IFileKeyRepository _fileKeyRepo = Substitute.For<IFileKeyRepository>();
    private readonly IStorageProviderRegistry _registry = Substitute.For<IStorageProviderRegistry>();
    private readonly IEncryptingFileWriter _encryptingWriter = Substitute.For<IEncryptingFileWriter>();
    private readonly IEncryptingFileWriterFactory _encryptingWriterFactory = Substitute.For<IEncryptingFileWriterFactory>();

    public FileDownloadResolverTests()
    {
        // Default wiring: any provider → the substituted writer. Tests that exercise the
        // encrypted-read path stub the registry to return a real (substitute) provider; the
        // factory then bridges from that provider to the writer the assertions inspect.
        _encryptingWriterFactory.Create(Arg.Any<IStorageProvider>()).Returns(_encryptingWriter);
    }

    private FileDownloadResolver Build() => new(
        _fileRepo, _driveRepo, _versionRepo, _fileKeyRepo, _registry, _encryptingWriterFactory);

    // ── failure paths ───────────────────────────────────────────────────────

    [Fact]
    public async Task DriveMissing_ReturnsNotFound()
    {
        _driveRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Drive?)null);

        var result = await Build().ResolveAsync(Guid.NewGuid(), Guid.NewGuid(), null, default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<DownloadFailure.NotFound>();
    }

    [Fact]
    public async Task FileMissing_ReturnsNotFound()
    {
        var driveId = Guid.NewGuid();
        StubDrive(driveId, encrypted: false);
        _fileRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((FileItem?)null);

        var result = await Build().ResolveAsync(driveId, Guid.NewGuid(), null, default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<DownloadFailure.NotFound>();
    }

    [Fact]
    public async Task FileOnDifferentDrive_ReturnsNotFound()
    {
        var driveAId = Guid.NewGuid();
        var driveBId = Guid.NewGuid();
        StubDrive(driveAId, encrypted: false);
        var file = SeedFile(driveBId, size: 100);
        _fileRepo.GetByIdAsync(file.Id, Arg.Any<CancellationToken>()).Returns(file);

        var result = await Build().ResolveAsync(driveAId, file.Id, null, default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<DownloadFailure.NotFound>();
    }

    [Fact]
    public async Task IsDirectory_ReturnsIsDirectory()
    {
        var driveId = Guid.NewGuid();
        StubDrive(driveId, encrypted: false);
        var dir = SeedFile(driveId, size: 0, isDirectory: true);
        _fileRepo.GetByIdAsync(dir.Id, Arg.Any<CancellationToken>()).Returns(dir);

        var result = await Build().ResolveAsync(driveId, dir.Id, null, default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<DownloadFailure.IsDirectory>();
    }

    [Fact]
    public async Task NullStorageKey_ReturnsNotFound()
    {
        // Half-uploaded rows expose 404 (same shape as unknown id) — wire contract must not
        // leak an "exists but pending" state.
        var driveId = Guid.NewGuid();
        StubDrive(driveId, encrypted: false);
        var file = SeedFile(driveId, size: 100, storageKey: null);
        _fileRepo.GetByIdAsync(file.Id, Arg.Any<CancellationToken>()).Returns(file);

        var result = await Build().ResolveAsync(driveId, file.Id, null, default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<DownloadFailure.NotFound>();
    }

    [Theory]
    [InlineData(100L, 200L, 299L)] // From >= size
    [InlineData(50L, 80L, 30L)]    // From > To (after clamp)
    public async Task RangeUnsatisfiable_FromBeyondOrInverted_ReturnsRangeNotSatisfiable(
        long size, long from, long to)
    {
        var (driveId, fileId) = SeedHappyPath(size, encrypted: false);

        var result = await Build().ResolveAsync(driveId, fileId, new DownloadRange(from, to), default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<DownloadFailure.RangeNotSatisfiable>()
            .Which.Size.Should().Be(size);
    }

    [Fact]
    public async Task RangeUnsatisfiable_SuffixZero_ReturnsRangeNotSatisfiable()
    {
        var (driveId, fileId) = SeedHappyPath(size: 100, encrypted: false);

        var result = await Build().ResolveAsync(driveId, fileId, new DownloadRange(null, 0), default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<DownloadFailure.RangeNotSatisfiable>()
            .Which.Size.Should().Be(100);
    }

    [Fact]
    public async Task RangeUnsatisfiable_ZeroLengthFile_ReturnsRangeNotSatisfiable()
    {
        var (driveId, fileId) = SeedHappyPath(size: 0, encrypted: false);

        var result = await Build().ResolveAsync(driveId, fileId, new DownloadRange(0, 99), default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<DownloadFailure.RangeNotSatisfiable>()
            .Which.Size.Should().Be(0);
    }

    [Fact]
    public async Task EncryptedFile_ZeroVersionCount_ReturnsInternalState()
    {
        var driveId = Guid.NewGuid();
        StubDrive(driveId, encrypted: true);
        var file = SeedFile(driveId, size: 100, versionCount: 0);
        _fileRepo.GetByIdAsync(file.Id, Arg.Any<CancellationToken>()).Returns(file);
        _registry.Resolve(Arg.Any<string>(), Arg.Any<IStorageProviderConfig>())
            .Returns(Substitute.For<IStorageProvider>());

        var result = await Build().ResolveAsync(driveId, file.Id, null, default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<DownloadFailure.InternalState>();
    }

    [Fact]
    public async Task EncryptedFile_MissingVersion_ReturnsInternalState()
    {
        var driveId = Guid.NewGuid();
        StubDrive(driveId, encrypted: true);
        var file = SeedFile(driveId, size: 100, versionCount: 1);
        _fileRepo.GetByIdAsync(file.Id, Arg.Any<CancellationToken>()).Returns(file);
        _registry.Resolve(Arg.Any<string>(), Arg.Any<IStorageProviderConfig>())
            .Returns(Substitute.For<IStorageProvider>());
        _versionRepo.GetAsync(file.Id, 1, Arg.Any<CancellationToken>()).Returns((FileVersion?)null);

        var result = await Build().ResolveAsync(driveId, file.Id, null, default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<DownloadFailure.InternalState>();
    }

    [Fact]
    public async Task EncryptedFile_MissingFileKey_ReturnsInternalState()
    {
        var driveId = Guid.NewGuid();
        StubDrive(driveId, encrypted: true);
        var file = SeedFile(driveId, size: 100, versionCount: 1);
        _fileRepo.GetByIdAsync(file.Id, Arg.Any<CancellationToken>()).Returns(file);
        _registry.Resolve(Arg.Any<string>(), Arg.Any<IStorageProviderConfig>())
            .Returns(Substitute.For<IStorageProvider>());
        var version = new FileVersion
        {
            FileId = file.Id, VersionNumber = 1, Size = 100, BlobSizeBytes = 100,
            ContentHash = "deadbeef", StorageKey = "drives/x/files/y/v1", CreatedBy = UserId,
        };
        _versionRepo.GetAsync(file.Id, 1, Arg.Any<CancellationToken>()).Returns(version);
        _fileKeyRepo.GetByFileVersionAsync(version.Id, Arg.Any<CancellationToken>()).Returns((FileKey?)null);

        var result = await Build().ResolveAsync(driveId, file.Id, null, default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<DownloadFailure.InternalState>();
    }

    // ── happy paths ─────────────────────────────────────────────────────────

    [Fact]
    public async Task PlaintextDrive_NoRange_ReturnsFullFile()
    {
        var (driveId, fileId) = SeedHappyPath(size: 100, encrypted: false);
        StubProviderReadStream(new byte[100]);

        var result = await Build().ResolveAsync(driveId, fileId, null, default);

        result.IsSuccess.Should().BeTrue();
        var dl = result.Value!;
        dl.Size.Should().Be(100);
        dl.IsPartial.Should().BeFalse();
        dl.PartialStart.Should().BeNull();
        dl.PartialEnd.Should().BeNull();
        dl.FileId.Should().Be(fileId);
        dl.DriveId.Should().Be(driveId);
        await dl.DisposeAsync();
    }

    [Fact]
    public async Task PlaintextDrive_BoundedRange_ReturnsPartialWithCorrectBounds()
    {
        var (driveId, fileId) = SeedHappyPath(size: 100, encrypted: false);
        StubProviderReadStream(new byte[100]);

        var result = await Build().ResolveAsync(driveId, fileId, new DownloadRange(10, 49), default);

        result.IsSuccess.Should().BeTrue();
        var dl = result.Value!;
        dl.IsPartial.Should().BeTrue();
        dl.PartialStart.Should().Be(10);
        dl.PartialEnd.Should().Be(49);
        dl.ResponseLength.Should().Be(40);
        await dl.DisposeAsync();
    }

    [Fact]
    public async Task PlaintextDrive_OpenEndedRange_ReturnsPartialThroughEnd()
    {
        var (driveId, fileId) = SeedHappyPath(size: 100, encrypted: false);
        StubProviderReadStream(new byte[100]);

        var result = await Build().ResolveAsync(driveId, fileId, new DownloadRange(75, null), default);

        result.IsSuccess.Should().BeTrue();
        var dl = result.Value!;
        dl.IsPartial.Should().BeTrue();
        dl.PartialStart.Should().Be(75);
        dl.PartialEnd.Should().Be(99);
        await dl.DisposeAsync();
    }

    [Fact]
    public async Task PlaintextDrive_SuffixRange_ReturnsLastN()
    {
        var (driveId, fileId) = SeedHappyPath(size: 100, encrypted: false);
        StubProviderReadStream(new byte[100]);

        var result = await Build().ResolveAsync(driveId, fileId, new DownloadRange(null, 30), default);

        result.IsSuccess.Should().BeTrue();
        var dl = result.Value!;
        dl.IsPartial.Should().BeTrue();
        dl.PartialStart.Should().Be(70);
        dl.PartialEnd.Should().Be(99);
        await dl.DisposeAsync();
    }

    [Fact]
    public async Task EncryptedDrive_HappyPath_ReturnsStreamAndUsesEncryptingReader()
    {
        var driveId = Guid.NewGuid();
        StubDrive(driveId, encrypted: true);
        var file = SeedFile(driveId, size: 100, versionCount: 1);
        _fileRepo.GetByIdAsync(file.Id, Arg.Any<CancellationToken>()).Returns(file);
        _registry.Resolve(Arg.Any<string>(), Arg.Any<IStorageProviderConfig>())
            .Returns(Substitute.For<IStorageProvider>());
        var version = new FileVersion
        {
            FileId = file.Id, VersionNumber = 1, Size = 100, BlobSizeBytes = 132,
            ContentHash = "deadbeef", StorageKey = file.StorageKey!, CreatedBy = UserId,
        };
        _versionRepo.GetAsync(file.Id, 1, Arg.Any<CancellationToken>()).Returns(version);
        _fileKeyRepo.GetByFileVersionAsync(version.Id, Arg.Any<CancellationToken>())
            .Returns(new FileKey
            {
                FileVersionId = version.Id,
                EncryptedDek = [0x01, 0x02, 0x03],
                Algorithm = "AES-256-GCM",
            });
        _encryptingWriter
            .ReadAsync(file.StorageKey!, Arg.Any<byte[]>(), "AES-256-GCM", 0, Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(new byte[100]));

        var result = await Build().ResolveAsync(driveId, file.Id, null, default);

        result.IsSuccess.Should().BeTrue();
        var dl = result.Value!;
        dl.Size.Should().Be(100);
        dl.IsPartial.Should().BeFalse();
        // Confirm the encrypted-read path was the one taken — provider.ReadAsync must NOT be called.
        _registry.Received(1).Resolve(Arg.Any<string>(), Arg.Any<IStorageProviderConfig>());
        await _encryptingWriter.Received(1).ReadAsync(
            file.StorageKey!, Arg.Any<byte[]>(), "AES-256-GCM", 0, Arg.Any<CancellationToken>());
        await dl.DisposeAsync();
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private void StubDrive(Guid driveId, bool encrypted) =>
        _driveRepo.GetByIdAsync(driveId, Arg.Any<CancellationToken>()).Returns(new Drive
        {
            Id = driveId,
            TenantId = TenantId,
            Name = "drive",
            ProviderType = "local",
            ProviderConfig = "{}",
            EncryptionEnabled = encrypted,
        });

    private static FileItem SeedFile(
        Guid driveId,
        long size,
        bool isDirectory = false,
        string? storageKey = "drives/x/files/y/v1",
        int versionCount = 1)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            DriveId = driveId,
            Name = "f.bin",
            Path = "/f.bin",
            Size = size,
            IsDirectory = isDirectory,
            StorageKey = storageKey,
            CreatedBy = UserId,
            VersionCount = versionCount,
            MimeType = "application/octet-stream",
        };

    private (Guid DriveId, Guid FileId) SeedHappyPath(long size, bool encrypted)
    {
        var driveId = Guid.NewGuid();
        StubDrive(driveId, encrypted);
        var file = SeedFile(driveId, size);
        _fileRepo.GetByIdAsync(file.Id, Arg.Any<CancellationToken>()).Returns(file);
        return (driveId, file.Id);
    }

    private void StubProviderReadStream(byte[] payload)
    {
        var provider = Substitute.For<IStorageProvider>();
        provider.ReadAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(payload));
        _registry.Resolve(Arg.Any<string>(), Arg.Any<IStorageProviderConfig>()).Returns(provider);
    }
}
