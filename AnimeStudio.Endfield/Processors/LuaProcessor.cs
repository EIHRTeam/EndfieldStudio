using System.Text;

namespace AnimeStudio.Endfield.Processors;

/// <summary>
/// Lua source decryption helper for files extracted from BlockType.Lua.
/// Faithful port of fluffy-dumper/fluffy-dumper/src/processors.rs (process_lua_file + normalize_newlines).
/// </summary>
public static class LuaProcessor
{
    /// <summary>
    /// Takes raw extracted bytes (a base64 ASCII text payload), base64-decodes,
    /// xxtea-decrypts, then normalizes newlines.
    /// </summary>
    public static byte[] DecryptAndNormalize(byte[] raw)
    {
        if (raw is null) throw new ArgumentNullException(nameof(raw));

        // Treat as text and trim whitespace, like Rust trim() on String::from_utf8_lossy.
        string content = Encoding.UTF8.GetString(raw).Trim();
        byte[] encrypted = Convert.FromBase64String(content);
        byte[] decrypted = Xxtea.Decrypt(encrypted, Keys.XxteaKey);
        return NormalizeNewlines(decrypted);
    }

    public static byte[] NormalizeNewlines(byte[] data)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));

        var output = new List<byte>(data.Length);
        bool seenNonWs = false;
        bool lastWasEmpty = false;

        foreach (byte b in data)
        {
            if (b == 0x0d) continue;

            if (b == 0x0a)
            {
                if (seenNonWs)
                {
                    output.Add(0x0a);
                    lastWasEmpty = false;
                }
                else if (!lastWasEmpty)
                {
                    output.Add(0x0a);
                    lastWasEmpty = true;
                }
                seenNonWs = false;
                continue;
            }

            if (b != 0x20 && b != 0x09)
            {
                seenNonWs = true;
            }

            output.Add(b);
            lastWasEmpty = false;
        }

        return output.ToArray();
    }
}
