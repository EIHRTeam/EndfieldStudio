using System.Globalization;
using System.Text;

namespace AnimeStudio.Endfield.Processors;

/// <summary>
/// SparkBuffer 二进制表 → JSON 解析器。
/// 忠实端口 fluffy-dumper/sparkbuffer/src/{lib,parser,types}.rs。
/// 格式：3 个偏移量头 → 类型定义区 → root 定义区 → 数据区。
/// </summary>
public static class SparkBuffer
{
    /// <summary>
    /// 解析 SparkBuffer 字节流，返回 (rootName, JSON 字符串)。
    /// </summary>
    public static (string Name, string Json) Parse(byte[] bytes)
    {
        if (bytes is null || bytes.Length < 12)
            throw new SparkBufferException("SparkBuffer 数据过短（< 12 字节头）");

        using var ms = new MemoryStream(bytes, writable: false);
        using var br = new SparkReader(ms);

        int typeDefOffset = br.ReadInt32LE();
        int rootDefOffset = br.ReadInt32LE();
        int dataOffset = br.ReadInt32LE();

        // 1. 类型定义区
        ms.Position = typeDefOffset;
        var registry = new TypeRegistry();
        ParseTypeDefinitions(br, registry);

        // 2. root 定义区
        ms.Position = rootDefOffset;
        RootDef rootDef = ParseRootDef(br);

        // 3. 数据区
        ms.Position = dataOffset;

        object? data = rootDef.FieldType switch
        {
            SparkType.Bean => ReadBeanValue(br, registry.GetBean(rootDef.TypeHash ?? 0), registry, isPointer: false),
            SparkType.Map => ReadRootMapValue(br, rootDef, registry),
            _ => throw new SparkBufferException($"不支持的根类型: {rootDef.FieldType}"),
        };

        // 自定义 JSON 序列化，精确匹配 serde_json::to_string_pretty 的格式
        // （System.Text.Json 的 double 格式化和 WriteRawValue 缩进都有问题）
        var sb = new StringBuilder(data is not null ? 4096 : 16);
        WriteJson(sb, data, indent: 0);
        string json = sb.ToString();
        return (rootDef.Name, json);
    }

    /// <summary>直接返回 pretty JSON 字符串。</summary>
    public static string ToJsonString(byte[] bytes) => Parse(bytes).Json;

    // ── 自定义 JSON 序列化（匹配 serde_json::to_string_pretty 格式） ──
    // 缩进 = 2 空格，double 整数值带 .0，null → null

    private static void WriteJson(StringBuilder sb, object? value, int indent)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case int i:
                sb.Append(i.ToString(CultureInfo.InvariantCulture));
                break;
            case long l:
                sb.Append(l.ToString(CultureInfo.InvariantCulture));
                break;
            case double d:
                WriteDouble(sb, d);
                break;
            case string s:
                WriteJsonString(sb, s);
                break;
            case SortedDictionary<string, object?> dict:
                WriteJsonObject(sb, dict, indent);
                break;
            case List<object?> list:
                WriteJsonArray(sb, list, indent);
                break;
            default:
                // Fallback for any other type
                sb.Append("null");
                break;
        }
    }

    private static void WriteJsonObject(StringBuilder sb, SortedDictionary<string, object?> dict, int indent)
    {
        if (dict.Count == 0)
        {
            sb.Append("{}");
            return;
        }

        sb.Append('{');
        bool first = true;
        foreach (var (key, val) in dict)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('\n').Append(' ', (indent + 1) * 2);
            WriteJsonString(sb, key);
            sb.Append(": ");
            WriteJson(sb, val, indent + 1);
        }
        sb.Append('\n').Append(' ', indent * 2).Append('}');
    }

    private static void WriteJsonArray(StringBuilder sb, List<object?> list, int indent)
    {
        if (list.Count == 0)
        {
            sb.Append("[]");
            return;
        }

        sb.Append('[');
        bool first = true;
        foreach (var item in list)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('\n').Append(' ', (indent + 1) * 2);
            WriteJson(sb, item, indent + 1);
        }
        sb.Append('\n').Append(' ', indent * 2).Append(']');
    }

    private static void WriteDouble(StringBuilder sb, double d)
    {
        // serde_json 行为：float 永远带小数点；科学计数法用小写 e 且指数无前导零
        if (double.IsFinite(d) && d == Math.Truncate(d))
        {
            sb.Append(d.ToString("0.0##############", CultureInfo.InvariantCulture));
        }
        else
        {
            // C# "R" 格式产生 "9.99E-07"（大写 E，指数有前导零）
            // Rust ryu 格式是 "9.99e-7"（小写 e，指数无前导零）
            string s = d.ToString("R", CultureInfo.InvariantCulture);
            int ePos = s.IndexOf('E');
            if (ePos >= 0)
            {
                // 转换 E+07 → e+7, E-07 → e-7
                string mantissa = s[..ePos];
                string exp = s[(ePos + 1)..];
                // 去掉指数前导零："-07" → "-7", "+07" → "+7" → "7"
                if (exp.Length > 1 && exp[0] == '+' && exp[1] == '0')
                    exp = exp[1..].TrimStart('0');
                else if (exp.Length > 2 && (exp[0] == '-' || exp[0] == '+') && exp[1] == '0')
                    exp = exp[0] + exp[1..].TrimStart('0');
                // ryu 格式正指数不带 +
                if (exp.StartsWith('+')) exp = exp[1..];
                sb.Append(mantissa).Append('e').Append(exp);
            }
            else
            {
                sb.Append(s);
            }
        }
    }

    private static void WriteJsonString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
    }

    // ── 类型定义解析 ──

    private static void ParseTypeDefinitions(SparkReader br, TypeRegistry registry)
    {
        int typeDefCount = br.ReadInt32LE();

        for (int i = 0; i < typeDefCount; i++)
        {
            var sparkType = br.ReadSparkType();
            br.Align4();

            switch (sparkType)
            {
                case SparkType.Enum:
                {
                    int typeHash = br.ReadInt32LE();
                    string name = br.ReadNullTerminatedString();
                    br.Align4();
                    int itemCount = br.ReadInt32LE();

                    var items = new List<EnumItem>(itemCount);
                    for (int j = 0; j < itemCount; j++)
                    {
                        string itemName = br.ReadNullTerminatedString();
                        br.Align4();
                        int itemValue = br.ReadInt32LE();
                        items.Add(new EnumItem(itemName, itemValue));
                    }

                    registry.InsertEnum(new EnumType(typeHash, name, items));
                    break;
                }
                case SparkType.Bean:
                {
                    int beanTypeHash = br.ReadInt32LE();
                    string name = br.ReadNullTerminatedString();
                    br.Align4();
                    int fieldCount = br.ReadInt32LE();

                    var fields = new List<BeanField>(fieldCount);
                    for (int j = 0; j < fieldCount; j++)
                    {
                        string fieldName = br.ReadNullTerminatedString();
                        var fieldType = br.ReadSparkType();

                        SparkType? type2 = null;
                        SparkType? type3 = null;
                        int? typeHash = null;
                        int? typeHash2 = null;

                        switch (fieldType)
                        {
                            case SparkType.Bool:
                            case SparkType.Byte:
                            case SparkType.Int:
                            case SparkType.Long:
                            case SparkType.Float:
                            case SparkType.Double:
                            case SparkType.String:
                                break;
                            case SparkType.Enum:
                            case SparkType.Bean:
                                br.Align4();
                                typeHash = br.ReadInt32LE();
                                break;
                            case SparkType.Array:
                                type2 = br.ReadSparkType();
                                if (type2.GetValueOrDefault().IsEnumOrBean())
                                {
                                    br.Align4();
                                    typeHash = br.ReadInt32LE();
                                }
                                break;
                            case SparkType.Map:
                                type2 = br.ReadSparkType();
                                type3 = br.ReadSparkType();
                                if (type2.GetValueOrDefault().IsEnumOrBean())
                                {
                                    br.Align4();
                                    typeHash = br.ReadInt32LE();
                                }
                                if (type3.GetValueOrDefault().IsEnumOrBean())
                                {
                                    br.Align4();
                                    typeHash2 = br.ReadInt32LE();
                                }
                                break;
                        }

                        fields.Add(new BeanField(
                            fieldName, fieldType, type2, type3, typeHash, typeHash2));
                    }

                    registry.InsertBean(new BeanType(beanTypeHash, name, fields));
                    break;
                }
                default:
                    throw new SparkBufferException($"类型定义区出现非 Enum/Bean 类型: {sparkType}");
            }
        }
    }

    private static RootDef ParseRootDef(SparkReader br)
    {
        var fieldType = br.ReadSparkType();
        string name = br.ReadNullTerminatedString();

        int? typeHash = null;
        SparkType? type2 = null;
        SparkType? type3 = null;
        int? typeHash2 = null;

        if (fieldType.IsEnumOrBean())
        {
            br.Align4();
            typeHash = br.ReadInt32LE();
        }

        if (fieldType == SparkType.Map)
        {
            type2 = br.ReadSparkType();
            type3 = br.ReadSparkType();

            if (type2.GetValueOrDefault().IsEnumOrBean())
            {
                br.Align4();
                typeHash = br.ReadInt32LE();
            }
            if (type3.GetValueOrDefault().IsEnumOrBean())
            {
                br.Align4();
                typeHash2 = br.ReadInt32LE();
            }
        }

        return new RootDef(fieldType, name, typeHash, type2, type3, typeHash2);
    }

    // ── 值读取 ──
    // 返回值约定：null 表示 JSON null；object? 是 primitives / SortedDictionary<string,object?> / List<object?>

    private static SortedDictionary<string, object?>? ReadBeanValue(
        SparkReader br, BeanType beanType, TypeRegistry registry, bool isPointer)
    {
        long pointerOrigin = -1;

        if (isPointer)
        {
            int beanOffset = br.ReadInt32LE();
            if (beanOffset == -1) return null;
            pointerOrigin = br.BaseStream.Position;
            br.BaseStream.Position = beanOffset;
        }

        var obj = new SortedDictionary<string, object?>(StringComparer.Ordinal);

        for (int i = 0; i < beanType.Fields.Count; i++)
        {
            var field = beanType.Fields[i];
            long origin = -1;

            if (field.FieldType == SparkType.Array)
            {
                int fieldOffset = br.ReadInt32LE();
                if (fieldOffset == -1)
                {
                    obj[field.Name] = null;
                    continue;
                }
                origin = br.BaseStream.Position;
                br.BaseStream.Position = fieldOffset;
            }

            object? value = field.FieldType switch
            {
                SparkType.Array => ReadArrayValue(br, field, registry),
                SparkType.Int => br.ReadInt32LE(),
                SparkType.Enum => br.ReadInt32LE(),
                SparkType.Long => br.ReadAlignedI64(),
                SparkType.Float => (double)br.ReadSingleLE(),
                SparkType.Double => br.ReadAlignedF64(),
                SparkType.String => (object?)br.ReadStringAtOffset(),
                SparkType.Bean => (object?)ReadBeanValue(br,
                    registry.GetBean(field.TypeHash ?? 0), registry, isPointer: true),
                SparkType.Bool => ReadBoolWithAlign(br, beanType.Fields, i),
                SparkType.Map => ReadMapValue(br, field, registry),
                SparkType.Byte => throw new SparkBufferException("Bean 内不支持 Byte 类型字段"),
                _ => throw new SparkBufferException($"不支持的字段类型: {field.FieldType}"),
            };

            obj[field.Name] = value;

            if (origin >= 0)
            {
                br.BaseStream.Position = origin;
            }
        }

        if (pointerOrigin >= 0)
        {
            br.BaseStream.Position = pointerOrigin;
        }

        return obj;
    }

    private static List<object?> ReadArrayValue(SparkReader br, BeanField field, TypeRegistry registry)
    {
        int itemCount = br.ReadInt32LE();
        var arr = new List<object?>(itemCount);

        var itemType = field.Type2
            ?? throw new SparkBufferException("Array 字段缺少 type2");

        for (int i = 0; i < itemCount; i++)
        {
            object? item = itemType switch
            {
                SparkType.String => (object?)br.ReadStringAtOffset(),
                SparkType.Bean => (object?)ReadBeanValue(br,
                    registry.GetBean(field.TypeHash ?? 0), registry, isPointer: true),
                SparkType.Float => (double)br.ReadSingleLE(),
                SparkType.Long => br.ReadAlignedI64(),
                SparkType.Int => br.ReadInt32LE(),
                SparkType.Enum => br.ReadInt32LE(),
                SparkType.Bool => br.ReadBooleanLE(),
                SparkType.Double => br.ReadAlignedF64(),
                _ => throw new SparkBufferException($"Array 不支持元素类型: {itemType}"),
            };
            arr.Add(item);
        }

        return arr;
    }

    private static SortedDictionary<string, object?>? ReadMapValue(
        SparkReader br, BeanField field, TypeRegistry registry)
    {
        int mapOffset = br.ReadInt32LE();
        long mapOrigin = br.BaseStream.Position;
        br.BaseStream.Position = mapOffset;

        var result = ReadMapEntries(br, field.Type2, field.Type3, field.TypeHash2, registry);

        br.BaseStream.Position = mapOrigin;
        return result;
    }

    private static SortedDictionary<string, object?> ReadRootMapValue(
        SparkReader br, RootDef rootDef, TypeRegistry registry)
    {
        int kvCount = br.ReadInt32LE();
        br.BaseStream.Seek((long)kvCount * 8, SeekOrigin.Current);

        var keyType = rootDef.Type2 ?? throw new SparkBufferException("root map 缺少 type2");
        var valueType = rootDef.Type3 ?? throw new SparkBufferException("root map 缺少 type3");

        return ReadMapKvPairs(br, kvCount, keyType, valueType, rootDef.TypeHash2, registry);
    }

    private static SortedDictionary<string, object?> ReadMapEntries(
        SparkReader br,
        SparkType? keyTypeOpt,
        SparkType? valueTypeOpt,
        int? typeHash2,
        TypeRegistry registry)
    {
        int kvCount = br.ReadInt32LE();
        br.BaseStream.Seek((long)kvCount * 8, SeekOrigin.Current);

        var keyType = keyTypeOpt ?? throw new SparkBufferException("map 缺少 type2");
        var valueType = valueTypeOpt ?? throw new SparkBufferException("map 缺少 type3");

        return ReadMapKvPairs(br, kvCount, keyType, valueType, typeHash2, registry);
    }

    private static SortedDictionary<string, object?> ReadMapKvPairs(
        SparkReader br,
        int kvCount,
        SparkType keyType,
        SparkType valueType,
        int? typeHash2,
        TypeRegistry registry)
    {
        var map = new SortedDictionary<string, object?>(StringComparer.Ordinal);

        for (int i = 0; i < kvCount; i++)
        {
            string key = ReadMapKey(br, keyType);
            object? value = ReadMapValueElement(br, valueType, typeHash2, registry, out bool isBool);
            map[key] = value;
            if (isBool) br.Align4();
        }

        return map;
    }

    private static string ReadMapKey(SparkReader br, SparkType keyType)
    {
        return keyType switch
        {
            SparkType.String => (string)br.ReadStringAtOffset()!,
            SparkType.Int => br.ReadInt32LE().ToString(CultureInfo.InvariantCulture),
            SparkType.Long => br.ReadAlignedI64().ToString(CultureInfo.InvariantCulture),
            _ => throw new SparkBufferException($"Map key 不支持类型: {keyType}"),
        };
    }

    private static object? ReadMapValueElement(
        SparkReader br,
        SparkType valueType,
        int? typeHash2,
        TypeRegistry registry,
        out bool isBool)
    {
        isBool = false;
        switch (valueType)
        {
            case SparkType.Bean:
                {
                    var bean = registry.GetBean(typeHash2 ?? 0);
                    return ReadBeanValue(br, bean, registry, isPointer: true);
                }
            case SparkType.String:
                return br.ReadStringAtOffset();
            case SparkType.Int:
                return br.ReadInt32LE();
            case SparkType.Float:
                return (double)br.ReadSingleLE();
            case SparkType.Enum:
                {
                    int val = br.ReadInt32LE();
                    var enumType = registry.GetEnum(typeHash2 ?? 0);
                    return enumType.GetName(val);
                }
            case SparkType.Bool:
                isBool = true;
                return br.ReadBooleanLE();
            default:
                throw new SparkBufferException($"Map value 不支持类型: {valueType}");
        }
    }

    // ── bool 读取后的对齐处理 ──

    private static object? ReadBoolWithAlign(SparkReader br, List<BeanField> fields, int currentIndex)
    {
        bool val = br.ReadBooleanLE();
        if (currentIndex + 1 < fields.Count && fields[currentIndex + 1].FieldType != SparkType.Bool)
        {
            br.Align4();
        }
        return val;
    }
}

public sealed class SparkBufferException : Exception
{
    public SparkBufferException(string message) : base(message) { }
    public SparkBufferException(string message, Exception inner) : base(message, inner) { }
}

// ── 类型定义 ──

internal enum SparkType : byte
{
    Bool = 0,
    Byte = 1,
    Int = 2,
    Long = 3,
    Float = 4,
    Double = 5,
    Enum = 6,
    String = 7,
    Bean = 8,
    Array = 9,
    Map = 10,
}

internal static class SparkTypeExtensions
{
    public static SparkType FromByte(byte value) => value switch
    {
        0 => SparkType.Bool,
        1 => SparkType.Byte,
        2 => SparkType.Int,
        3 => SparkType.Long,
        4 => SparkType.Float,
        5 => SparkType.Double,
        6 => SparkType.Enum,
        7 => SparkType.String,
        8 => SparkType.Bean,
        9 => SparkType.Array,
        10 => SparkType.Map,
        _ => throw new SparkBufferException($"无效 SparkType: {value}"),
    };

    public static bool IsEnumOrBean(this SparkType t) => t == SparkType.Enum || t == SparkType.Bean;
}

internal sealed record EnumItem(string Name, int Value);

internal sealed record EnumType(int TypeHash, string Name, List<EnumItem> Items)
{
    public string GetName(int value)
    {
        foreach (var item in Items)
        {
            if (item.Value == value) return item.Name;
        }
        return value.ToString(CultureInfo.InvariantCulture);
    }
}

internal sealed record BeanField(
    string Name,
    SparkType FieldType,
    SparkType? Type2,
    SparkType? Type3,
    int? TypeHash,
    int? TypeHash2);

internal sealed record BeanType(int TypeHash, string Name, List<BeanField> Fields);

internal sealed record RootDef(
    SparkType FieldType,
    string Name,
    int? TypeHash,
    SparkType? Type2,
    SparkType? Type3,
    int? TypeHash2);

internal sealed class TypeRegistry
{
    private readonly Dictionary<int, BeanType> _beans = new();
    private readonly Dictionary<int, EnumType> _enums = new();

    public BeanType GetBean(int hash)
        => _beans.TryGetValue(hash, out var b) ? b
         : throw new SparkBufferException($"未知 Bean type hash: 0x{hash:X8}");

    public EnumType GetEnum(int hash)
        => _enums.TryGetValue(hash, out var e) ? e
         : throw new SparkBufferException($"未知 Enum type hash: 0x{hash:X8}");

    public void InsertBean(BeanType bean) => _beans[bean.TypeHash] = bean;
    public void InsertEnum(EnumType enumType) => _enums[enumType.TypeHash] = enumType;
}

// ── BinaryReader 辅助扩展（忠实端口 Rust 的 read_*_le + align） ──
// 用 SparkReader 包装 BinaryReader，保持 API 与 Rust reader 对等。

internal sealed class SparkReader : IDisposable
{
    private readonly BinaryReader _br;
    public Stream BaseStream => _br.BaseStream;

    public SparkReader(Stream stream) => _br = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

    public int ReadInt32LE() => _br.ReadInt32();
    public long ReadAlignedI64() { Align8(); return _br.ReadInt64(); }
    public float ReadSingleLE() => _br.ReadSingle();
    public double ReadAlignedF64() { Align8(); return _br.ReadDouble(); }
    public bool ReadBooleanLE() => _br.ReadByte() != 0;

    public SparkType ReadSparkType() => SparkTypeExtensions.FromByte(_br.ReadByte());

    public string ReadNullTerminatedString()
    {
        var bytes = new List<byte>(32);
        while (true)
        {
            int b = _br.ReadByte();
            if (b == 0) break;
            bytes.Add((byte)b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    public string? ReadStringAtOffset()
    {
        int offset = ReadInt32LE();
        if (offset == -1) return "";  // 匹配 Rust read_string_at_offset: offset=-1 → String::new()
        long oldPos = BaseStream.Position;
        BaseStream.Position = offset;
        string s = ReadNullTerminatedString();
        BaseStream.Position = oldPos;
        return s;
    }

    public void Align4()
    {
        long pos = BaseStream.Position;
        // Rust: aligned = (pos - 1) + (4 - ((pos - 1) % 4))
        // 等价于向上对齐到 4 的倍数（当 pos 已经对齐时不动）
        long aligned = (pos - 1 + 4) & ~3L;
        BaseStream.Position = aligned;
    }

    public void Align8()
    {
        long pos = BaseStream.Position;
        long aligned = (pos - 1 + 8) & ~7L;
        BaseStream.Position = aligned;
    }

    public void Dispose() => _br.Dispose();
}

