using Course.Shared;
using Xunit;

namespace Course.Tests;

/// <summary>
/// Граница пересылаемого ответа на шлюзе.
///
/// Решение «передано целиком или нет» принимается здесь, а цена ошибки в обе стороны высока:
/// лишний байт — и шлюз режет законный ответ; пропущенное превышение — и граница не существует.
/// Поэтому проверяются ровно края: пусто, ровно предел, на байт больше, многократно больше.
/// </summary>
public sealed class BoundedCopyTests
{
    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 10)]
    [InlineData(10, 10)]
    [InlineData(200_000, 200_000)]
    public async Task ASourceWithinTheLimitIsCopiedWhole(int length, int limit)
    {
        var payload = Payload(length);
        using var destination = new MemoryStream();

        var complete = await BoundedCopy.CopyAsync(new MemoryStream(payload), destination, limit, CancellationToken.None);

        Assert.True(complete);
        Assert.Equal(payload, destination.ToArray());
    }

    /// <summary>
    /// Один байт сверх предела — уже превышение. И записано при этом не больше предела: неполное
    /// содержимое не должно дорасти до объёма, который граница и запрещает.
    /// </summary>
    [Theory]
    [InlineData(11, 10)]
    [InlineData(200_001, 200_000)]
    [InlineData(1_000_000, 100)]
    public async Task ASourceBeyondTheLimitIsReportedIncomplete(int length, int limit)
    {
        using var destination = new MemoryStream();

        var complete = await BoundedCopy.CopyAsync(new MemoryStream(Payload(length)), destination, limit, CancellationToken.None);

        Assert.False(complete);
        Assert.True(destination.Length <= limit, $"записано {destination.Length} байт при пределе {limit}");
    }

    [Fact]
    public async Task ANegativeLimitIsRejected()
        => await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => BoundedCopy.CopyAsync(new MemoryStream(), new MemoryStream(), -1, CancellationToken.None));

    private static byte[] Payload(int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < length; index++)
        {
            bytes[index] = (byte)(index % 251);
        }

        return bytes;
    }
}
