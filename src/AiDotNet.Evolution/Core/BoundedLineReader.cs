using System.Text;

namespace AiDotNet.Evolution;

/// <summary>Reads newline-terminated UTF-8 frames from a stream, refusing any frame longer than a bound.</summary>
/// <remarks>It buffers past the end of a frame, so one reader must own a stream for its whole life.</remarks>
internal sealed class BoundedLineReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _start;
    private int _end;

    internal BoundedLineReader(Stream stream, int maximumLineBytes)
    {
        _stream = stream;
        MaximumLineBytes = maximumLineBytes;
    }

    /// <summary>Gets or sets the longest accepted frame, in bytes, excluding the newline.</summary>
    internal int MaximumLineBytes { get; set; }

    /// <summary>Reads the next frame, or returns <c>null</c> at a clean end of stream.</summary>
    /// <exception cref="InvalidDataException">The frame exceeds the bound, is not UTF-8, or the stream ends mid-frame.</exception>
    internal async Task<string?> ReadLineAsync()
    {
        using var line = new MemoryStream();
        while (true)
        {
            int newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            int take = (newline >= 0 ? newline : _end) - _start;
            if (line.Length + take > MaximumLineBytes)
                throw new InvalidDataException($"A frame exceeded {MaximumLineBytes} bytes.");
            line.Write(_buffer, _start, take);
            if (newline >= 0)
            {
                _start = newline + 1;
                byte[] bytes = line.ToArray();
                int length = bytes.Length > 0 && bytes[bytes.Length - 1] == '\r' ? bytes.Length - 1 : bytes.Length;
                try
                {
                    return StrictUtf8.GetString(bytes, 0, length);
                }
                catch (DecoderFallbackException exception)
                {
                    throw new InvalidDataException("A frame is not valid UTF-8.", exception);
                }
            }

            _start = 0;
            _end = await _stream.ReadAsync(_buffer, 0, _buffer.Length).ConfigureAwait(false);
            if (_end == 0)
            {
                if (line.Length == 0) return null;
                throw new InvalidDataException("The stream ended in the middle of a frame.");
            }
        }
    }
}
