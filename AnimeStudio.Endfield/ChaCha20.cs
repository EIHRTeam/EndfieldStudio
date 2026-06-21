using System.Buffers.Binary;

namespace AnimeStudio.Endfield;

/// <summary>
/// ChaCha20 stream cipher (RFC 8439 / Bernstein).
/// Faithful port of fluffy-dumper/chacha20/src/lib.rs.
/// Supports multi-call streaming via leftover keystream buffer.
/// </summary>
public sealed class ChaCha20
{
    private const int StateLength = 16;
    private const int BlockSize = 64;

    private readonly uint[] _state = new uint[StateLength];
    private readonly byte[] _leftoverBlock = new byte[BlockSize];
    private int _leftoverLen;
    private int _leftoverOffset;

    public ChaCha20(ReadOnlySpan<byte> key32, ReadOnlySpan<byte> nonce12, uint counter)
    {
        if (key32.Length != 32) throw new ArgumentException("key must be 32 bytes", nameof(key32));
        if (nonce12.Length != 12) throw new ArgumentException("nonce must be 12 bytes", nameof(nonce12));

        // "expand 32-byte k"
        _state[0] = 0x61707865u;
        _state[1] = 0x3320646eu;
        _state[2] = 0x79622d32u;
        _state[3] = 0x6b206574u;

        _state[4] = BinaryPrimitives.ReadUInt32LittleEndian(key32.Slice(0, 4));
        _state[5] = BinaryPrimitives.ReadUInt32LittleEndian(key32.Slice(4, 4));
        _state[6] = BinaryPrimitives.ReadUInt32LittleEndian(key32.Slice(8, 4));
        _state[7] = BinaryPrimitives.ReadUInt32LittleEndian(key32.Slice(12, 4));
        _state[8] = BinaryPrimitives.ReadUInt32LittleEndian(key32.Slice(16, 4));
        _state[9] = BinaryPrimitives.ReadUInt32LittleEndian(key32.Slice(20, 4));
        _state[10] = BinaryPrimitives.ReadUInt32LittleEndian(key32.Slice(24, 4));
        _state[11] = BinaryPrimitives.ReadUInt32LittleEndian(key32.Slice(28, 4));

        _state[12] = counter;

        _state[13] = BinaryPrimitives.ReadUInt32LittleEndian(nonce12.Slice(0, 4));
        _state[14] = BinaryPrimitives.ReadUInt32LittleEndian(nonce12.Slice(4, 4));
        _state[15] = BinaryPrimitives.ReadUInt32LittleEndian(nonce12.Slice(8, 4));

        _leftoverLen = 0;
        _leftoverOffset = 0;
    }

    public void ApplyKeystream(Span<byte> data)
    {
        int dataPos = 0;

        // 1. Drain leftover keystream from previous call.
        if (_leftoverLen > 0 && data.Length > 0)
        {
            int take = Math.Min(_leftoverLen, data.Length);
            for (int i = 0; i < take; i++)
            {
                data[dataPos + i] ^= _leftoverBlock[_leftoverOffset + i];
            }
            _leftoverOffset += take;
            _leftoverLen -= take;
            dataPos += take;
        }

        // 2. Process full blocks directly into data.
        Span<uint> working = stackalloc uint[StateLength];
        Span<byte> keystream = stackalloc byte[BlockSize];

        while (data.Length - dataPos >= BlockSize)
        {
            GenerateBlock(working, keystream);
            for (int i = 0; i < BlockSize; i++)
            {
                data[dataPos + i] ^= keystream[i];
            }
            dataPos += BlockSize;
        }

        // 3. Tail: generate one more block, save remainder.
        int tail = data.Length - dataPos;
        if (tail > 0)
        {
            GenerateBlock(working, keystream);
            for (int i = 0; i < tail; i++)
            {
                data[dataPos + i] ^= keystream[i];
            }
            // Save the unused tail of the keystream block for next call.
            int leftover = BlockSize - tail;
            keystream.Slice(tail, leftover).CopyTo(_leftoverBlock.AsSpan(0, leftover));
            _leftoverOffset = 0;
            _leftoverLen = leftover;
        }
    }

    private void GenerateBlock(Span<uint> working, Span<byte> output)
    {
        for (int i = 0; i < StateLength; i++) working[i] = _state[i];

        for (int i = 0; i < 10; i++)
        {
            QuarterRound(working, 0, 4, 8, 12);
            QuarterRound(working, 1, 5, 9, 13);
            QuarterRound(working, 2, 6, 10, 14);
            QuarterRound(working, 3, 7, 11, 15);

            QuarterRound(working, 0, 5, 10, 15);
            QuarterRound(working, 1, 6, 11, 12);
            QuarterRound(working, 2, 7, 8, 13);
            QuarterRound(working, 3, 4, 9, 14);
        }

        for (int i = 0; i < StateLength; i++)
        {
            uint sum = working[i] + _state[i];
            BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(i * 4, 4), sum);
        }

        unchecked
        {
            _state[12] += 1;
            if (_state[12] == 0)
            {
                _state[13] += 1;
            }
        }
    }

    private static void QuarterRound(Span<uint> x, int a, int b, int c, int d)
    {
        unchecked
        {
            x[a] += x[b];
            x[d] = RotateLeft(x[d] ^ x[a], 16);

            x[c] += x[d];
            x[b] = RotateLeft(x[b] ^ x[c], 12);

            x[a] += x[b];
            x[d] = RotateLeft(x[d] ^ x[a], 8);

            x[c] += x[d];
            x[b] = RotateLeft(x[b] ^ x[c], 7);
        }
    }

    private static uint RotateLeft(uint value, int count)
        => (value << count) | (value >> (32 - count));
}
