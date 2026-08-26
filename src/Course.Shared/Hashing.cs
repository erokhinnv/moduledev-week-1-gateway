using System.Security.Cryptography;
using System.Text;

namespace Course.Shared;

/// <summary>Вспомогательные функции хеширования.</summary>
public static class Hashing
{
    /// <summary>
    /// Возвращает SHA-256 хеш данных в виде строки из 64 шестнадцатеричных символов нижнего
    /// регистра. Это единственная форма, в которой содержимое запроса (payload) когда-либо
    /// публикуется наружу (например в проверочных views autocheck) — сам payload целиком туда
    /// не попадает, только его "отпечаток".
    /// </summary>
    public static string Sha256Hex(ReadOnlySpan<byte> value)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(value, digest);
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>Перегрузка для строк: сначала кодирует строку в UTF-8, затем хеширует байты.</summary>
    public static string Sha256Hex(string value) => Sha256Hex(Encoding.UTF8.GetBytes(value));
}
