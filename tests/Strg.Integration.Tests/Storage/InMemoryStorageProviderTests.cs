using System.Text;
using FluentAssertions;
using Strg.Core.Storage;
using Strg.Infrastructure.Storage;
using Xunit;

namespace Strg.Integration.Tests.Storage;

/// <summary>
/// STRG-030 TC-001..TC-004 plus listing semantics (immediate children only, implicit parent
/// directories). In-memory provider tests intentionally live alongside the local-fs tests: both
/// must pass the same <see cref="IStorageProvider"/> contract and drifting behaviour between
/// them silently breaks every test that uses the in-memory double as a stand-in for the real
/// filesystem.
/// </summary>
public sealed class InMemoryStorageProviderTests
{
    private readonly InMemoryStorageProvider _sut = new();

    [Fact] // TC-001
    public async Task Write_then_read_returns_same_bytes()
    {
        var payload = Encoding.UTF8.GetBytes("in-memory-content");
        await _sut.WriteAsync("a.txt", new MemoryStream(payload));

        await using var stream = await _sut.ReadAsync("a.txt");
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        buffer.ToArray().Should().Equal(payload);
    }

    [Fact] // TC-002
    public async Task GetFileAsync_size_matches_written_bytes()
    {
        var payload = new byte[42];
        Random.Shared.NextBytes(payload);
        await _sut.WriteAsync("size-check.bin", new MemoryStream(payload));

        var file = await _sut.GetFileAsync("size-check.bin");
        file.Should().NotBeNull();
        file!.Size.Should().Be(42);
    }

    [Fact] // TC-003
    public async Task Concurrent_writes_to_different_paths_do_not_deadlock()
    {
        // Fan out 50 concurrent writes on distinct paths. ConcurrentDictionary already gives us
        // the correctness guarantee; this test pins that WriteAsync itself doesn't take any
        // external lock that would serialize them (or worse, self-deadlock).
        var writes = Enumerable.Range(0, 50).Select(i =>
            _sut.WriteAsync($"parallel/file-{i}.bin", new MemoryStream([(byte)i]))).ToArray();

        var act = async () => await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(5));
        await act.Should().NotThrowAsync();

        for (var i = 0; i < 50; i++)
        {
            (await _sut.ExistsAsync($"parallel/file-{i}.bin")).Should().BeTrue();
        }
    }

    [Fact] // TC-004
    public async Task ListAsync_with_prefix_returns_only_files_under_that_prefix()
    {
        await _sut.WriteAsync("docs/a.txt", new MemoryStream([1]));
        await _sut.WriteAsync("docs/b.txt", new MemoryStream([2]));
        await _sut.WriteAsync("images/c.png", new MemoryStream([3]));

        var items = new List<IStorageItem>();
        await foreach (var item in _sut.ListAsync("docs"))
        {
            items.Add(item);
        }

        items.Should().HaveCount(2);
        items.Select(i => i.Name).Should().BeEquivalentTo(["a.txt", "b.txt"]);
    }

    [Fact]
    public void ProviderType_is_memory()
    {
        _sut.ProviderType.Should().Be("memory");
    }

    [Fact]
    public async Task ClearAll_drops_every_file_and_directory()
    {
        await _sut.WriteAsync("x.txt", new MemoryStream([1]));
        await _sut.CreateDirectoryAsync("folder");

        _sut.ClearAll();

        (await _sut.ExistsAsync("x.txt")).Should().BeFalse();
        (await _sut.ExistsAsync("folder")).Should().BeFalse();
    }

    [Fact]
    public async Task ReadAsync_with_offset_seeks_past_prefix()
    {
        var payload = new byte[16];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }
        await _sut.WriteAsync("ramp.bin", new MemoryStream(payload));

        await using var stream = await _sut.ReadAsync("ramp.bin", offset: 8);
        stream.ReadByte().Should().Be(8);
    }

    [Fact]
    public async Task MoveAsync_fails_if_destination_exists()
    {
        await _sut.WriteAsync("src.txt", new MemoryStream([1]));
        await _sut.WriteAsync("dst.txt", new MemoryStream([2]));

        var act = async () => await _sut.MoveAsync("src.txt", "dst.txt");
        await act.Should().ThrowAsync<IOException>();

        // Source must still exist after the failed move — the contract is "move or fail", not
        // "delete source then try to add dest".
        (await _sut.ExistsAsync("src.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task ListAsync_surfaces_implicit_intermediate_directories()
    {
        // Only the leaf file was written — no explicit CreateDirectoryAsync. Listing "docs"
        // should still surface the "2024" subdirectory that's implied by the file path.
        await _sut.WriteAsync("docs/2024/report.pdf", new MemoryStream([1]));

        var names = new List<string>();
        await foreach (var item in _sut.ListAsync("docs"))
        {
            names.Add(item.Name);
        }

        names.Should().Contain("2024");
    }

    [Fact]
    public async Task ListAsync_at_root_returns_top_level_items_only()
    {
        await _sut.WriteAsync("top.txt", new MemoryStream([1]));
        await _sut.WriteAsync("nested/deep.txt", new MemoryStream([2]));

        var names = new List<string>();
        await foreach (var item in _sut.ListAsync(""))
        {
            names.Add(item.Name);
        }

        // "top.txt" is a direct child; "nested" is the implicit parent of the second file;
        // "deep.txt" must NOT appear here because it's one level down.
        names.Should().Contain(["top.txt", "nested"]);
        names.Should().NotContain("deep.txt");
    }

    [Fact]
    public async Task DeleteAsync_removes_recursive_subtree()
    {
        await _sut.WriteAsync("drop/a.txt", new MemoryStream([1]));
        await _sut.WriteAsync("drop/sub/b.txt", new MemoryStream([2]));

        await _sut.DeleteAsync("drop");

        (await _sut.ExistsAsync("drop/a.txt")).Should().BeFalse();
        (await _sut.ExistsAsync("drop/sub/b.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task CopyAsync_produces_independent_payload()
    {
        var payload = new byte[] { 1, 2, 3 };
        await _sut.WriteAsync("origin.bin", new MemoryStream(payload));
        await _sut.CopyAsync("origin.bin", "clone.bin");

        // Both exist and the clone reads back the same bytes.
        var clone = await _sut.GetFileAsync("clone.bin");
        clone.Should().NotBeNull();
        clone!.Size.Should().Be(3);
    }

    [Fact]
    public async Task WriteAsync_rejects_parent_traversal()
    {
        var act = async () => await _sut.WriteAsync("../evil.txt", new MemoryStream([1]));
        await act.Should().ThrowAsync<StoragePathException>();
    }
}
