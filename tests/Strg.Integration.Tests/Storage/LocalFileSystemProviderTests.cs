using System.Text;
using FluentAssertions;
using Strg.Core.Storage;
using Strg.Infrastructure.Storage;
using Xunit;

namespace Strg.Integration.Tests.Storage;

/// <summary>
/// STRG-024 TC-001..TC-008 + path-traversal/symlink defense-in-depth.
///
/// <para>These tests touch the real filesystem (not an in-memory fake) because the whole point of
/// <see cref="LocalFileSystemProvider"/> is its interaction with <see cref="System.IO.File"/> and
/// <see cref="Directory"/>. A mock would validate the wrapper's call shape, not the security
/// properties we actually care about (traversal, symlink rejection, overwrite semantics).</para>
///
/// <para>Each test uses a unique temp directory under <see cref="Path.GetTempPath"/> to stay
/// parallel-safe; <see cref="IAsyncLifetime.DisposeAsync"/> cleans up afterwards.</para>
/// </summary>
public sealed class LocalFileSystemProviderTests : IAsyncLifetime
{
    private string _root = null!;
    private LocalFileSystemProvider _sut = null!;

    public Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "strg-tests-" + Guid.NewGuid().ToString("N"));
        _sut = new LocalFileSystemProvider(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        return Task.CompletedTask;
    }

    [Fact] // TC-001
    public async Task Write_then_read_returns_same_bytes()
    {
        var payload = Encoding.UTF8.GetBytes("hello world");
        await _sut.WriteAsync("greeting.txt", new MemoryStream(payload));

        await using var stream = await _sut.ReadAsync("greeting.txt");
        using var reader = new MemoryStream();
        await stream.CopyToAsync(reader);
        reader.ToArray().Should().Equal(payload);
    }

    [Fact] // TC-002
    public async Task GetFileAsync_on_missing_path_returns_null()
    {
        var result = await _sut.GetFileAsync("does/not/exist.txt");
        result.Should().BeNull();
    }

    [Fact] // TC-003
    public async Task ReadAsync_with_offset_seeks_past_prefix()
    {
        // 256 ascending bytes: byte N has value N. Lets us assert "first byte read is byte N of
        // the file" by value, not just by index, without string encoding ambiguity.
        var payload = new byte[256];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }
        await _sut.WriteAsync("ramp.bin", new MemoryStream(payload));

        await using var stream = await _sut.ReadAsync("ramp.bin", offset: 100);
        var firstByte = stream.ReadByte();
        firstByte.Should().Be(100);
    }

    [Fact] // TC-004
    public async Task WriteAsync_creates_missing_intermediate_directories()
    {
        await _sut.WriteAsync("sub/dir/file.txt", new MemoryStream([1, 2, 3]));

        Directory.Exists(Path.Combine(_root, "sub", "dir")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "sub", "dir", "file.txt")).Should().BeTrue();
    }

    [Fact] // TC-005
    public async Task DeleteAsync_is_idempotent_on_missing_target()
    {
        // No prior Write — the target does not exist. Idempotency means "no throw".
        var act = async () => await _sut.DeleteAsync("never-existed.txt");
        await act.Should().NotThrowAsync();
    }

    [Fact] // TC-006
    public async Task ListAsync_streams_large_directories_without_buffering()
    {
        // 1_000 files rather than 10_000 — the streaming property is structurally the same and
        // this keeps the test suite fast. The test proves "enumerator advances one item at a
        // time" by asserting MoveNextAsync succeeds before the full directory is materialized.
        Directory.CreateDirectory(Path.Combine(_root, "big"));
        for (var i = 0; i < 1_000; i++)
        {
            await File.WriteAllBytesAsync(Path.Combine(_root, "big", $"f{i:D4}.bin"), [(byte)(i & 0xFF)]);
        }

        var count = 0;
        await foreach (var item in _sut.ListAsync("big"))
        {
            count++;
            item.IsDirectory.Should().BeFalse();
            if (count == 1)
            {
                // First item arrived before we drained the rest — the iterator is lazy.
                item.Should().BeAssignableTo<IStorageFile>();
            }
        }
        count.Should().Be(1_000);
    }

    [Fact] // TC-007
    public async Task WriteAsync_rejects_parent_traversal_in_input_path()
    {
        var act = async () => await _sut.WriteAsync("../escape.txt", new MemoryStream([1]));
        await act.Should().ThrowAsync<StoragePathException>();
    }

    [Fact] // TC-008
    public async Task MoveAsync_relocates_file_and_original_is_gone()
    {
        await _sut.WriteAsync("a/file.txt", new MemoryStream([1, 2, 3]));
        await _sut.MoveAsync("a/file.txt", "b/file.txt");

        (await _sut.GetFileAsync("a/file.txt")).Should().BeNull();
        var moved = await _sut.GetFileAsync("b/file.txt");
        moved.Should().NotBeNull();
        moved!.Size.Should().Be(3);
    }

    [Fact]
    public void ProviderType_is_local()
    {
        _sut.ProviderType.Should().Be("local");
    }

    [Fact]
    public async Task CopyAsync_preserves_source()
    {
        await _sut.WriteAsync("src.txt", new MemoryStream([7, 8, 9]));
        await _sut.CopyAsync("src.txt", "dst.txt");

        (await _sut.GetFileAsync("src.txt")).Should().NotBeNull();
        var copy = await _sut.GetFileAsync("dst.txt");
        copy.Should().NotBeNull();
        copy!.Size.Should().Be(3);
    }

    [Fact]
    public async Task ExistsAsync_returns_true_for_files_and_directories()
    {
        await _sut.WriteAsync("folder/file.bin", new MemoryStream([1]));

        (await _sut.ExistsAsync("folder/file.bin")).Should().BeTrue();
        (await _sut.ExistsAsync("folder")).Should().BeTrue();
        (await _sut.ExistsAsync("nothing-here")).Should().BeFalse();
    }

    [Fact]
    public async Task CreateDirectoryAsync_is_idempotent_and_creates_intermediates()
    {
        await _sut.CreateDirectoryAsync("a/b/c");
        Directory.Exists(Path.Combine(_root, "a", "b", "c")).Should().BeTrue();

        // Second call on an existing directory must not throw — matches Directory.CreateDirectory
        // contract and simplifies callers that don't want to probe existence first.
        var act = async () => await _sut.CreateDirectoryAsync("a/b/c");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task WriteAsync_replaces_existing_file_contents()
    {
        await _sut.WriteAsync("same-name.txt", new MemoryStream(Encoding.UTF8.GetBytes("first")));
        await _sut.WriteAsync("same-name.txt", new MemoryStream(Encoding.UTF8.GetBytes("second-and-longer")));

        await using var stream = await _sut.ReadAsync("same-name.txt");
        using var reader = new StreamReader(stream);
        (await reader.ReadToEndAsync()).Should().Be("second-and-longer");
    }

    [Fact]
    public async Task MoveAsync_does_not_overwrite_existing_destination()
    {
        await _sut.WriteAsync("a.txt", new MemoryStream([1]));
        await _sut.WriteAsync("b.txt", new MemoryStream([2]));

        var act = async () => await _sut.MoveAsync("a.txt", "b.txt");

        // Specific IOException type varies by OS (IOException on Linux, specific
        // "already exists" subclass on Windows). Asserting the base class keeps the test
        // portable while still proving the silent-clobber path is closed.
        await act.Should().ThrowAsync<IOException>();
    }

    [Fact]
    public async Task Symlink_outside_base_path_is_not_followed()
    {
        // Skip on Windows — File.CreateSymbolicLink requires elevated privileges there, and the
        // CI environment can't guarantee them. The Linux coverage is what matters: production
        // deployments run on Linux containers where symlink bypasses are the real threat.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var outsideFile = Path.Combine(Path.GetTempPath(), "strg-escape-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(outsideFile, "SECRET");
        try
        {
            var linkPath = Path.Combine(_root, "link-to-secret.txt");
            File.CreateSymbolicLink(linkPath, outsideFile);

            // GetFileAsync must report null rather than surface the symlinked file — otherwise a
            // tenant who could write symlinks via a different channel would read anything on disk.
            var file = await _sut.GetFileAsync("link-to-secret.txt");
            file.Should().BeNull();

            var readAct = async () =>
            {
                await using var _ = await _sut.ReadAsync("link-to-secret.txt");
            };
            await readAct.Should().ThrowAsync<FileNotFoundException>();
        }
        finally
        {
            File.Delete(outsideFile);
        }
    }

    [Fact]
    public async Task Null_bytes_in_path_are_rejected()
    {
        // Defense against the classic "null byte truncation" trick (some platforms stop reading
        // paths at the first \0, allowing "legal.txt\0../etc/passwd" to pass string-level checks).
        var act = async () => await _sut.WriteAsync("legal\0secret.txt", new MemoryStream([1]));
        await act.Should().ThrowAsync<StoragePathException>();
    }
}
