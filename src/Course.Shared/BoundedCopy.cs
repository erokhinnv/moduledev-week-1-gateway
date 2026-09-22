using System.Buffers;

namespace Course.Shared;

/// <summary>
/// Копирование потока с жёсткой верхней границей.
///
/// Нужно там, где один узел переливает чужой ответ и при этом отвечает за собственную память и
/// канал: ограничение, объявленное источником, — это его обещание, а не гарантия. Граница
/// проверяется ДО записи очередного фрагмента, поэтому в приёмник никогда не уходит больше, чем
/// разрешено.
/// </summary>
public static class BoundedCopy
{
    private const int ChunkBytes = 81_920;

    /// <summary>
    /// Копирует <paramref name="source"/> в <paramref name="destination"/>, пока не будет
    /// превышен <paramref name="limitBytes"/>.
    /// </summary>
    /// <returns>
    /// <c>true</c>, если источник передан целиком; <c>false</c>, если он длиннее границы. Во втором
    /// случае в приёмник записано не больше <paramref name="limitBytes"/> байт, и вызывающий обязан
    /// считать переданное неполным — молча выдать его за целое нельзя.
    /// </returns>
    public static async Task<bool> CopyAsync(
        Stream source,
        Stream destination,
        long limitBytes,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limitBytes);

        var buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        try
        {
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, ChunkBytes), cancellationToken)) > 0)
            {
                if (written + read > limitBytes)
                {
                    return false;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                written += read;
            }

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
