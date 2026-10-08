using System.Runtime.CompilerServices;
using System.Text;
using AnimeStudio.Endfield.Processors;
using AS = AnimeStudio;
using CliProgram = AnimeStudio.Endfield.Cli.Program;

internal static class OriginalTextureLuaRegression
{
    public static void Run()
    {
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        var texture = (AS.Texture2D)RuntimeHelpers.GetUninitializedObject(typeof(AS.Texture2D));
        texture.m_TextureFormat = AS.TextureFormat.RGBA32;
        texture.m_MipCount = 1;
        texture.image_data = new AS.ResourceReader(new BinaryReader(new MemoryStream()), 0, 0);
        texture.m_StreamData = (AS.StreamingInfo)RuntimeHelpers.GetUninitializedObject(typeof(AS.StreamingInfo));
        texture.m_StreamData.path = "";
        Check(CliProgram.OriginalTextureRawOnlyReason(texture, true) == "empty_texture", "A fully parsed 0x0 font atlas with no payload must preserve its raw record");
        Check(CliProgram.OriginalTextureRawOnlyReason(texture, false) is null, "An incompletely parsed texture must not be accepted as an empty atlas");
        texture.m_Width = 1;
        Check(CliProgram.OriginalTextureRawOnlyReason(texture, true) is null, "One zero dimension does not establish an empty atlas");
        texture.m_Width = 0;
        texture.m_CompleteImageSize = 4;
        Check(CliProgram.OriginalTextureRawOnlyReason(texture, true) is null, "A declared but missing image must retain its error");
        texture.m_CompleteImageSize = 0;
        texture.m_StreamData.path = "missing.resS";
        Check(CliProgram.OriginalTextureRawOnlyReason(texture, true) is null, "A stream reference must not be silently accepted as an empty atlas");
        texture.m_StreamData.path = "";
        texture.m_StreamData.size = 4;
        Check(CliProgram.OriginalTextureRawOnlyReason(texture, true) is null, "An undecoded external stream payload must retain its error");
        texture.m_StreamData.size = 0;
        texture.image_data = new AS.ResourceReader(new BinaryReader(new MemoryStream(new byte[4])), 0, 4);
        Check(CliProgram.OriginalTextureRawOnlyReason(texture, true) is null, "An undecoded inline pixel payload must retain its error");

        byte[] markdown = Encoding.UTF8.GetBytes("# 文档\r\n\r\n`Source.lua`\r\n");
        Check(LuaProcessor.ProcessVfsFile("Data/LuaScripts/Source.md", markdown).SequenceEqual(markdown), "Non-Lua files in the Lua block must preserve every byte, including CRLF");
        Check(!LuaProcessor.IsLuaSource("Data/LuaScripts/Source.md") && LuaProcessor.IsLuaSource("Data/LuaScripts/Source.LUA"), "Lua decryption depends on the source extension, case-insensitively");
        bool rejected = false;
        try { LuaProcessor.ProcessVfsFile("Data/LuaScripts/Source.lua", markdown); }
        catch (FormatException) { rejected = true; }
        Check(rejected, "Invalid encrypted Lua must not silently fall back to unprocessed text");
        Console.WriteLine("PASS: strict empty texture classification and exact non-Lua payload preservation");
    }
}
