using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Strg.Core.Storage;
using Strg.Infrastructure.Storage;
using Strg.Infrastructure.Storage.Encryption;
using Xunit;

namespace Strg.Api.Tests.Storage;

/// <summary>
/// These tests live or die by a narrow definition: <see cref="AesGcmFileWriter"/> produces a
/// ciphertext envelope on the inner provider that does NOT contain the plaintext, and the same
/// writer (plus the wrapped DEK) round-trips back to the exact plaintext. Anything weaker —
/// structural checks alone, integrity-tag-only checks — passes even if the crypto is subtly
/// broken. So every test below either (a) verifies the plaintext is invisible from raw storage
/// OR (b) verifies the plaintext is recoverable from the documented API entry points.
/// </summary>
public sealed class AesGcmFileWriterTests
{
    private static readonly string ValidKekBase64 = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    [Fact]
    public async Task WriteAsync_then_ReadAsync_roundtrips_plaintext()
    {
        var (writer, _) = CreateWriter();
        var plaintext = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog.");

        var writeResult = await writer.WriteAsync("blob.txt", new MemoryStream(plaintext));

        await using var readStream = await writer.ReadAsync("blob.txt", writeResult.WrappedDek);
        using var buffer = new MemoryStream();
        await readStream.CopyToAsync(buffer);

        buffer.ToArray().Should().Equal(plaintext);
        writeResult.Algorithm.Should().Be("AES-256-GCM");
        writeResult.Length.Should().Be(plaintext.Length);
    }

    [Fact]
    public async Task Ciphertext_on_inner_provider_does_not_contain_plaintext()
    {
        // If the writer is broken enough to pass-through plaintext, every other test still passes
        // but exfiltrated storage is plaintext. This is the one test that catches that failure.
        var (writer, inner) = CreateWriter();
        var plaintext = Encoding.UTF8.GetBytes("SECRET_TOKEN_ABCDEFG_1234567890");

        await writer.WriteAsync("leak.txt", new MemoryStream(plaintext));

        await using var ciphertextStream = await inner.ReadAsync("leak.txt");
        using var buffer = new MemoryStream();
        await ciphertextStream.CopyToAsync(buffer);
        var ciphertext = buffer.ToArray();

        ContainsSubsequence(ciphertext, plaintext).Should().BeFalse(
            "plaintext bytes must NOT appear anywhere in the envelope on disk");
    }

    [Fact]
    public async Task Two_writes_of_same_plaintext_produce_different_ciphertexts()
    {
        // Fresh DEK per write + random file_nonce per write → even identical inputs produce
        // distinct envelopes. Violation indicates either DEK reuse or nonce reuse — both are
        // AES-GCM catastrophic failures.
        var (writer, inner) = CreateWriter();
        var plaintext = Encoding.UTF8.GetBytes("same bytes every time");

        await writer.WriteAsync("a.txt", new MemoryStream(plaintext));
        await writer.WriteAsync("b.txt", new MemoryStream(plaintext));

        var a = await ReadAllInnerAsync(inner, "a.txt");
        var b = await ReadAllInnerAsync(inner, "b.txt");
        a.Should().NotEqual(b);
    }

    [Fact]
    public async Task Write_produces_unique_wrapped_DEK_per_file()
    {
        var (writer, _) = CreateWriter();
        var plaintext = new byte[] { 1, 2, 3 };

        var resultA = await writer.WriteAsync("a.txt", new MemoryStream(plaintext));
        var resultB = await writer.WriteAsync("b.txt", new MemoryStream(plaintext));

        resultA.WrappedDek.Should().NotEqual(resultB.WrappedDek);
    }

    [Fact]
    public async Task ReadAsync_detects_ciphertext_tampering()
    {
        var (writer, inner) = CreateWriter();
        var plaintext = Encoding.UTF8.GetBytes("authenticated payload");

        var result = await writer.WriteAsync("file.txt", new MemoryStream(plaintext));

        // Envelope: header(20) + ciphertext(21) + tag(16) = 57 bytes. Middle byte (28) lands in
        // the ciphertext region — flip should break the final-chunk tag.
        var envelope = await ReadAllInnerAsync(inner, "file.txt");
        envelope[envelope.Length / 2] ^= 0xFF;
        await inner.WriteAsync("file.txt", new MemoryStream(envelope));

        var act = async () =>
        {
            await using var stream = await writer.ReadAsync("file.txt", result.WrappedDek);
            using var buf = new MemoryStream();
            await stream.CopyToAsync(buf);
        };
        await act.Should().ThrowAsync<AuthenticationTagMismatchException>();
    }

    [Fact]
    public async Task ReadAsync_detects_nonce_tampering()
    {
        // The GCM tag is computed over (nonce, AAD, ciphertext). AAD carries the whole file_nonce
        // (envelope bytes 8..19), so flipping any byte in that window must produce a tag mismatch —
        // otherwise we'd accept envelopes whose nonce was re-targeted under the same DEK, a
        // catastrophic GCM failure mode.
        var (writer, inner) = CreateWriter();
        var plaintext = Encoding.UTF8.GetBytes("nonce-tamper payload");

        var result = await writer.WriteAsync("file.txt", new MemoryStream(plaintext));

        var envelope = await ReadAllInnerAsync(inner, "file.txt");
        envelope[10] ^= 0xFF; // inside the 12-byte file_nonce region (envelope bytes 8..19)
        await inner.WriteAsync("file.txt", new MemoryStream(envelope));

        var act = async () =>
        {
            await using var stream = await writer.ReadAsync("file.txt", result.WrappedDek);
            using var buf = new MemoryStream();
            await stream.CopyToAsync(buf);
        };
        await act.Should().ThrowAsync<AuthenticationTagMismatchException>();
    }

    [Fact]
    public async Task ReadAsync_detects_file_nonce_padding_tampering()
    {
        // Bytes 4..11 of file_nonce are NOT consumed by the per-chunk nonce derivation (which
        // only uses bytes 0..3). Without AAD binding, tampering them would slip through silently.
        // This test pins down that the AAD covers the full 12-byte file_nonce, not just the 4-byte
        // salt used in the nonce formula.
        var (writer, inner) = CreateWriter();
        var plaintext = Encoding.UTF8.GetBytes("padding-tamper payload");

        var result = await writer.WriteAsync("file.txt", new MemoryStream(plaintext));

        var envelope = await ReadAllInnerAsync(inner, "file.txt");
        envelope[15] ^= 0xFF; // inside file_nonce bytes 4..11 (not used by nonce formula)
        await inner.WriteAsync("file.txt", new MemoryStream(envelope));

        var act = async () =>
        {
            await using var stream = await writer.ReadAsync("file.txt", result.WrappedDek);
            using var buf = new MemoryStream();
            await stream.CopyToAsync(buf);
        };
        await act.Should().ThrowAsync<AuthenticationTagMismatchException>();
    }

    [Fact]
    public async Task ReadAsync_detects_tag_tampering()
    {
        // The trailing 16 bytes are the final chunk's GCM tag. Flipping any byte there is the
        // cheapest possible forgery attempt — the library must still reject it. Paired with the
        // nonce and mid-ciphertext tamper tests, this pins down that EVERY region of the envelope
        // is authenticated, not just the ciphertext body.
        var (writer, inner) = CreateWriter();
        var plaintext = Encoding.UTF8.GetBytes("tag-tamper payload");

        var result = await writer.WriteAsync("file.txt", new MemoryStream(plaintext));

        var envelope = await ReadAllInnerAsync(inner, "file.txt");
        envelope[^1] ^= 0xFF; // last byte of the 16-byte final-chunk tag
        await inner.WriteAsync("file.txt", new MemoryStream(envelope));

        var act = async () =>
        {
            await using var stream = await writer.ReadAsync("file.txt", result.WrappedDek);
            using var buf = new MemoryStream();
            await stream.CopyToAsync(buf);
        };
        await act.Should().ThrowAsync<AuthenticationTagMismatchException>();
    }

    [Fact]
    public async Task ReadAsync_detects_chunk_truncation()
    {
        // Drop the final chunk of a two-chunk envelope. The previous chunk was encoded with
        // is_final=0 in its AAD, so replaying it against is_final=1 AAD on read (because the
        // reader now sees it as the last chunk) must fail the tag check. This is the whole point
        // of binding is_final into the AAD.
        var (writer, inner) = CreateWriter();
        var plaintext = new byte[130 * 1024]; // 3 chunks: 64K + 64K + 2K
        RandomNumberGenerator.Fill(plaintext);

        var result = await writer.WriteAsync("big.bin", new MemoryStream(plaintext));

        var envelope = await ReadAllInnerAsync(inner, "big.bin");
        // Chop the final (2K + 16-byte tag) chunk.
        var truncated = envelope.AsSpan(0, envelope.Length - (2 * 1024 + 16)).ToArray();
        await inner.WriteAsync("big.bin", new MemoryStream(truncated));

        var act = async () =>
        {
            await using var stream = await writer.ReadAsync("big.bin", result.WrappedDek);
            using var buf = new MemoryStream();
            await stream.CopyToAsync(buf);
        };
        await act.Should().ThrowAsync<AuthenticationTagMismatchException>();
    }

    [Fact]
    public async Task Concurrent_writes_and_reads_all_roundtrip()
    {
        // Regression guard: if a future refactor ever introduces shared mutable state on the
        // writer (e.g., a pooled envelope buffer, a cached AesGcm instance), this test is
        // engineered to surface the corruption — each iteration uses a distinct payload and
        // asserts byte-exact recovery. 200 iterations is enough to shake loose rare races without
        // making the suite annoyingly slow (fewer than the old single-shot version because each
        // roundtrip is heavier now).
        var (writer, _) = CreateWriter();
        const int iterations = 200;

        var plaintexts = new byte[iterations][];
        for (var i = 0; i < iterations; i++)
        {
            plaintexts[i] = Encoding.UTF8.GetBytes($"concurrent-payload-{i:D4}");
        }

        var writeResults = new EncryptedWriteResult[iterations];
        await Parallel.ForEachAsync(Enumerable.Range(0, iterations), async (i, ct) =>
        {
            writeResults[i] = await writer.WriteAsync($"concurrent/file-{i:D4}.bin", new MemoryStream(plaintexts[i]), ct);
        });

        await Parallel.ForEachAsync(Enumerable.Range(0, iterations), async (i, ct) =>
        {
            await using var stream = await writer.ReadAsync($"concurrent/file-{i:D4}.bin", writeResults[i].WrappedDek, cancellationToken: ct);
            using var buf = new MemoryStream();
            await stream.CopyToAsync(buf, ct);
            buf.ToArray().Should().Equal(plaintexts[i]);
        });
    }

    [Fact]
    public async Task ReadAsync_detects_tampered_wrapped_DEK()
    {
        var (writer, _) = CreateWriter();
        var plaintext = Encoding.UTF8.GetBytes("hello");

        var result = await writer.WriteAsync("file.txt", new MemoryStream(plaintext));
        var tamperedWrappedDek = result.WrappedDek.ToArray();
        tamperedWrappedDek[0] ^= 0xFF;

        var act = async () =>
        {
            await using var stream = await writer.ReadAsync("file.txt", tamperedWrappedDek);
            using var buf = new MemoryStream();
            await stream.CopyToAsync(buf);
        };
        await act.Should().ThrowAsync<AuthenticationTagMismatchException>();
    }

    [Fact]
    public async Task ReadAsync_with_wrong_KEK_fails_authentication()
    {
        // Same ciphertext, different KEK on read — the wrapped DEK can't be unwrapped, so the
        // KEK check fires before any ciphertext is decrypted. Critical for KEK-rotation-gone-wrong.
        var (writerA, _, innerA) = CreateWriterWithInner(ValidKekBase64);
        var plaintext = Encoding.UTF8.GetBytes("cross-key test");
        var result = await writerA.WriteAsync("file.txt", new MemoryStream(plaintext));

        // Copy the ciphertext into a second inner provider so writerB can see the same bytes.
        var envelope = await ReadAllInnerAsync(innerA, "file.txt");
        var differentKek = new byte[32];
        RandomNumberGenerator.Fill(differentKek);
        var (writerB, _, innerB) = CreateWriterWithInner(Convert.ToBase64String(differentKek));
        await innerB.WriteAsync("file.txt", new MemoryStream(envelope));

        var act = async () =>
        {
            await using var stream = await writerB.ReadAsync("file.txt", result.WrappedDek);
            using var buf = new MemoryStream();
            await stream.CopyToAsync(buf);
        };
        await act.Should().ThrowAsync<AuthenticationTagMismatchException>();
    }

    [Fact]
    public async Task ReadAsync_with_offset_returns_plaintext_suffix()
    {
        var (writer, _) = CreateWriter();
        var plaintext = Encoding.UTF8.GetBytes("0123456789ABCDEF");

        var result = await writer.WriteAsync("f.txt", new MemoryStream(plaintext));

        await using var stream = await writer.ReadAsync("f.txt", result.WrappedDek, offset: 6);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);

        buffer.ToArray().Should().Equal(Encoding.UTF8.GetBytes("6789ABCDEF"));
    }

    [Fact]
    public async Task ReadAsync_offset_past_end_returns_empty_stream()
    {
        var (writer, _) = CreateWriter();
        var plaintext = Encoding.UTF8.GetBytes("short");

        var result = await writer.WriteAsync("f.txt", new MemoryStream(plaintext));

        await using var stream = await writer.ReadAsync("f.txt", result.WrappedDek, offset: 999);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);

        buffer.Length.Should().Be(0);
    }

    [Fact]
    public async Task ReadAsync_negative_offset_throws()
    {
        var (writer, _) = CreateWriter();
        var act = () => writer.ReadAsync("x", new byte[60], offset: -1);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ReadAsync_on_truncated_envelope_throws_invalid_data()
    {
        // Envelope shorter than the 20-byte header can't be well-formed — reject before handing
        // any bytes to AesGcm so the error message is actionable. Use a legitimate wrapped DEK so
        // the KEK-unwrap check (which fires BEFORE the envelope read) passes.
        var (writer, inner) = CreateWriter();
        var good = await writer.WriteAsync("good", new MemoryStream(new byte[] { 1, 2, 3 }));

        // Overwrite "good" with a too-short blob so the wrapped DEK is still valid but the
        // envelope fails its length check.
        await inner.WriteAsync("good", new MemoryStream(new byte[10]));

        var act = async () =>
        {
            await using var stream = await writer.ReadAsync("good", good.WrappedDek);
            using var buf = new MemoryStream();
            await stream.CopyToAsync(buf);
        };
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task ReadAsync_detects_magic_tampering()
    {
        // Header with an unknown magic is almost certainly either a corrupted strg envelope or a
        // different file format that slipped into an encrypted drive. Reject fast with a clear
        // error rather than letting AesGcm surface a tag mismatch on random header bytes.
        var (writer, inner) = CreateWriter();
        var good = await writer.WriteAsync("good", new MemoryStream(new byte[] { 1, 2, 3 }));

        var envelope = await ReadAllInnerAsync(inner, "good");
        envelope[0] ^= 0xFF; // flip first magic byte
        await inner.WriteAsync("good", new MemoryStream(envelope));

        var act = async () =>
        {
            await using var stream = await writer.ReadAsync("good", good.WrappedDek);
            using var buf = new MemoryStream();
            await stream.CopyToAsync(buf);
        };
        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*magic bytes*");
    }

    [Fact]
    public async Task Empty_file_roundtrips_correctly()
    {
        // Zero-byte plaintext still produces an explicit final chunk: header(20) + tag(16) = 36
        // bytes. Without that trailing tag the reader couldn't tell "legitimate empty file" from
        // "envelope truncated after header" — both would look like 20 bytes.
        var (writer, inner) = CreateWriter();
        var result = await writer.WriteAsync("empty.bin", new MemoryStream(Array.Empty<byte>()));

        var envelope = await ReadAllInnerAsync(inner, "empty.bin");
        envelope.Should().HaveCount(36); // 8 magic + 12 file_nonce + 0 ciphertext + 16 tag
        result.Length.Should().Be(0);

        await using var stream = await writer.ReadAsync("empty.bin", result.WrappedDek);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        buffer.Length.Should().Be(0);
    }

    [Fact]
    public async Task Multi_chunk_file_roundtrips_correctly()
    {
        // 130 KiB straddles two full chunks + a partial final chunk — exercises rotation of the
        // lookahead buffers AND the is_final=false → is_final=true transition. A roundtrip failure
        // here (but not on single-chunk files) almost always means the AAD is wrong on either the
        // penultimate or the final chunk.
        var (writer, _) = CreateWriter();
        var plaintext = new byte[130 * 1024];
        RandomNumberGenerator.Fill(plaintext);

        var writeResult = await writer.WriteAsync("big.bin", new MemoryStream(plaintext));

        await using var readStream = await writer.ReadAsync("big.bin", writeResult.WrappedDek);
        using var buffer = new MemoryStream();
        await readStream.CopyToAsync(buffer);

        buffer.ToArray().Should().Equal(plaintext);
        writeResult.Length.Should().Be(plaintext.Length);
    }

    [Fact]
    public async Task Chunk_aligned_file_roundtrips_correctly()
    {
        // A file whose length is an exact multiple of the chunk size is the nastiest edge for
        // lookahead-one-chunk streamers: the writer must still emit a 16-byte tag-only final
        // chunk for the last index to satisfy the "every file ends with is_final=1" invariant.
        var (writer, _) = CreateWriter();
        var plaintext = new byte[64 * 1024 * 2]; // exactly two full chunks
        RandomNumberGenerator.Fill(plaintext);

        var writeResult = await writer.WriteAsync("aligned.bin", new MemoryStream(plaintext));

        await using var readStream = await writer.ReadAsync("aligned.bin", writeResult.WrappedDek);
        using var buffer = new MemoryStream();
        await readStream.CopyToAsync(buffer);

        buffer.ToArray().Should().Equal(plaintext);
    }

    [Fact]
    public async Task Very_large_file_roundtrip()
    {
        // 10 MiB = 160 × 64-KiB chunks. The point isn't just correctness at scale — it's to prove
        // that neither the encrypt nor decrypt path accumulates plaintext or ciphertext in memory
        // proportional to file size. If a reviewer ever introduces buffering (say, a helpful
        // "MemoryStream everything first" shortcut), this test won't fail functionally but the
        // stream-based assertion here at least exercises the real production path.
        var (writer, _) = CreateWriter();
        var plaintext = new byte[10 * 1024 * 1024];
        RandomNumberGenerator.Fill(plaintext);

        var writeResult = await writer.WriteAsync("xl.bin", new MemoryStream(plaintext));

        await using var readStream = await writer.ReadAsync("xl.bin", writeResult.WrappedDek);
        using var buffer = new MemoryStream(capacity: plaintext.Length);
        await readStream.CopyToAsync(buffer);

        buffer.ToArray().Should().Equal(plaintext);
        writeResult.Length.Should().Be(plaintext.Length);
    }

    [Fact]
    public async Task SkipBytesAsync_on_tampered_early_chunk_throws()
    {
        // The skip-to-offset path in AesGcmFileWriter.ReadAsync MUST authenticate the chunks it
        // skips, not just jump past them — otherwise a malicious inner provider could corrupt an
        // early chunk knowing the caller's Range request starts past it. We tamper chunk 0's
        // ciphertext byte, then ask for a read that skips past chunk 0. If skip-read bypassed
        // authentication, the read would succeed returning chunk-1 plaintext; it must instead
        // throw AuthenticationTagMismatchException during the skip.
        var (writer, inner) = CreateWriter();
        var plaintext = new byte[AesGcmFileWriter.ChunkPlaintextSize * 2];
        RandomNumberGenerator.Fill(plaintext);
        var result = await writer.WriteAsync("two-chunk.bin", new MemoryStream(plaintext));

        // First chunk ciphertext starts at byte 20 (header = 8 magic + 12 file_nonce).
        var envelope = await ReadAllInnerAsync(inner, "two-chunk.bin");
        envelope[AesGcmFileWriter.HeaderLength] ^= 0x01; // flip one bit in chunk 0 ciphertext
        await inner.WriteAsync("two-chunk.bin", new MemoryStream(envelope));

        var act = async () =>
        {
            await using var stream = await writer.ReadAsync(
                "two-chunk.bin",
                result.WrappedDek,
                offset: AesGcmFileWriter.ChunkPlaintextSize + 1);
            using var buf = new MemoryStream();
            await stream.CopyToAsync(buf);
        };
        await act.Should().ThrowAsync<AuthenticationTagMismatchException>();
    }

    [Fact]
    public async Task WriteAsync_rejects_null_content()
    {
        var (writer, _) = CreateWriter();
        var act = () => writer.WriteAsync("x", null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task WriteAsync_rejects_empty_storage_key()
    {
        var (writer, _) = CreateWriter();
        var act = () => writer.WriteAsync("", new MemoryStream());
        await act.Should().ThrowAsync<ArgumentException>();
    }

    private static (IEncryptingFileWriter writer, InMemoryStorageProvider inner) CreateWriter()
    {
        var (writer, _, inner) = CreateWriterWithInner(ValidKekBase64);
        return (writer, inner);
    }

    private static (IEncryptingFileWriter writer, IKeyProvider keys, InMemoryStorageProvider inner) CreateWriterWithInner(string kekBase64)
    {
        var inner = new InMemoryStorageProvider();
        var keys = new EnvVarKeyProvider(kekBase64);
        var writer = new AesGcmFileWriter(inner, keys);
        return (writer, keys, inner);
    }

    private static async Task<byte[]> ReadAllInnerAsync(InMemoryStorageProvider inner, string path)
    {
        await using var stream = await inner.ReadAsync(path);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return needle.Length == 0;
        }
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
            {
                return true;
            }
        }
        return false;
    }
}
