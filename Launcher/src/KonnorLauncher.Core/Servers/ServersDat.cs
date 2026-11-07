using System.Buffers.Binary;
using System.Text;

namespace KonnorLauncher.Core.Servers;

/// <summary>
/// Lista de servidores del juego (<c>servers.dat</c> en la carpeta de la instancia, la de «Multijugador»).
/// Es NBT sin comprimir: un compuesto raíz con la lista <c>servers</c> de compuestos <c>name</c>/<c>ip</c>.
/// Se lee entero y se vuelve a escribir igual, así se conserva lo que haya (iconos, otros servidores y
/// cualquier etiqueta desconocida); solo se añade el servidor que falte.
/// </summary>
public static class ServersDat
{
    public const string FileName = "servers.dat";

    private const byte TagEnd = 0, TagByte = 1, TagShort = 2, TagInt = 3, TagLong = 4, TagFloat = 5, TagDouble = 6,
        TagByteArray = 7, TagString = 8, TagList = 9, TagCompound = 10, TagIntArray = 11, TagLongArray = 12;

    /// <summary>Entrada de la lista: nombre e «ip» (la dirección tal cual, con el puerto si lo lleva).</summary>
    public sealed record Entry(string Name, string Address);

    /// <summary>Servidores de la lista, en orden. Vacía si el archivo no existe.</summary>
    public static IReadOnlyList<Entry> Read(string gameDir)
    {
        var root = Load(Path.Combine(gameDir, FileName));
        return Servers(root).Select(s => new Entry(GetString(s, "name") ?? string.Empty, GetString(s, "ip") ?? string.Empty))
            .Where(e => e.Address.Length > 0).ToList();
    }

    /// <summary>
    /// Añade el servidor al principio de la lista si no está ya (misma dirección, sin distinguir mayúsculas).
    /// Devuelve false si ya estaba. Escribe pasando por un temporal.
    /// </summary>
    public static bool Add(string gameDir, string name, ServerAddress address)
    {
        var path = Path.Combine(gameDir, FileName);
        var root = Load(path);
        var list = ServersList(root);
        var ip = address.ToString();
        if (list.Items.OfType<Compound>().Any(s => ServerAddress.TryParse(GetString(s, "ip"), out var existing) && existing == address))
            return false;

        var entry = new Compound();
        entry.Set("name", TagString, string.IsNullOrWhiteSpace(name) ? ip : name.Trim());
        entry.Set("ip", TagString, ip);
        list.Items.Insert(0, entry);

        Directory.CreateDirectory(gameDir);
        var temp = path + ".tmp";
        using (var file = File.Create(temp))
            WriteNamed(file, TagCompound, string.Empty, root);
        File.Move(temp, path, overwrite: true);
        return true;
    }

    // ── Modelo mínimo de NBT ─────────────────────────────────────────────

    private sealed class Compound
    {
        public List<(string Name, byte Type, object Value)> Tags { get; } = [];

        public object? Get(string name, byte type) => Tags.FirstOrDefault(t => t.Name == name && t.Type == type).Value;

        public void Set(string name, byte type, object value)
        {
            Tags.RemoveAll(t => t.Name == name);
            Tags.Add((name, type, value));
        }
    }

    private sealed class NbtList(byte type)
    {
        public byte Type { get; set; } = type;
        public List<object> Items { get; } = [];
    }

    private static string? GetString(Compound compound, string name) => compound.Get(name, TagString) as string;

    private static IEnumerable<Compound> Servers(Compound root) =>
        root.Get("servers", TagList) is NbtList { Type: TagCompound } list ? list.Items.OfType<Compound>() : [];

    /// <summary>La lista <c>servers</c> (se crea si falta o no es de compuestos).</summary>
    private static NbtList ServersList(Compound root)
    {
        if (root.Get("servers", TagList) is NbtList list && (list.Type == TagCompound || list.Items.Count == 0))
        {
            list.Type = TagCompound;
            return list;
        }

        var created = new NbtList(TagCompound);
        root.Set("servers", TagList, created);
        return created;
    }

    /// <summary>Raíz del archivo; vacía si no existe o está dañado (el juego lo trata igual).</summary>
    private static Compound Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new Compound();
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            return reader.ReadByte() == TagCompound
                ? (ReadPayload(reader, TagCompound, ReadString(reader), 0) as Compound ?? new Compound())
                : new Compound();
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or InvalidDataException or ArgumentException)
        {
            return new Compound();
        }
    }

    private static object ReadPayload(BinaryReader r, byte type, string _, int depth)
    {
        if (depth > 64)
            throw new InvalidDataException("NBT demasiado anidado");
        switch (type)
        {
            case TagByte: return r.ReadSByte();
            case TagShort: return BinaryPrimitives.ReadInt16BigEndian(r.ReadBytes(2));
            case TagInt: return ReadInt(r);
            case TagLong: return BinaryPrimitives.ReadInt64BigEndian(r.ReadBytes(8));
            case TagFloat: return BinaryPrimitives.ReadSingleBigEndian(r.ReadBytes(4));
            case TagDouble: return BinaryPrimitives.ReadDoubleBigEndian(r.ReadBytes(8));
            case TagByteArray: return r.ReadBytes(Length(r, 1));
            case TagString: return ReadString(r);
            case TagList:
            {
                var list = new NbtList(r.ReadByte());
                var count = Length(r, 1);
                for (var i = 0; i < count; i++)
                    list.Items.Add(ReadPayload(r, list.Type, string.Empty, depth + 1));
                return list;
            }
            case TagCompound:
            {
                var compound = new Compound();
                while (true)
                {
                    var child = r.ReadByte();
                    if (child == TagEnd)
                        return compound;
                    var name = ReadString(r);
                    compound.Tags.Add((name, child, ReadPayload(r, child, name, depth + 1)));
                }
            }
            case TagIntArray:
            {
                var values = new int[Length(r, 4)];
                for (var i = 0; i < values.Length; i++)
                    values[i] = ReadInt(r);
                return values;
            }
            case TagLongArray:
            {
                var values = new long[Length(r, 8)];
                for (var i = 0; i < values.Length; i++)
                    values[i] = BinaryPrimitives.ReadInt64BigEndian(r.ReadBytes(8));
                return values;
            }
            default:
                throw new InvalidDataException($"Etiqueta NBT desconocida: {type}");
        }
    }

    private static int ReadInt(BinaryReader r) => BinaryPrimitives.ReadInt32BigEndian(r.ReadBytes(4));

    /// <summary>Longitud de un arreglo o lista, comprobada contra lo que queda del archivo.</summary>
    private static int Length(BinaryReader r, int itemBytes)
    {
        var length = ReadInt(r);
        if (length < 0 || (long)length * itemBytes > r.BaseStream.Length - r.BaseStream.Position)
            throw new InvalidDataException("Longitud NBT no válida");
        return length;
    }

    private static string ReadString(BinaryReader r)
    {
        var length = BinaryPrimitives.ReadUInt16BigEndian(r.ReadBytes(2));
        var bytes = r.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException();
        return Encoding.UTF8.GetString(bytes);
    }

    private static void WriteNamed(Stream s, byte type, string name, object value)
    {
        s.WriteByte(type);
        WriteString(s, name);
        WritePayload(s, type, value);
    }

    private static void WritePayload(Stream s, byte type, object value)
    {
        Span<byte> buffer = stackalloc byte[8];
        switch (type)
        {
            case TagByte: s.WriteByte(unchecked((byte)(sbyte)value)); break;
            case TagShort: BinaryPrimitives.WriteInt16BigEndian(buffer, (short)value); s.Write(buffer[..2]); break;
            case TagInt: WriteInt(s, (int)value); break;
            case TagLong: BinaryPrimitives.WriteInt64BigEndian(buffer, (long)value); s.Write(buffer[..8]); break;
            case TagFloat: BinaryPrimitives.WriteSingleBigEndian(buffer, (float)value); s.Write(buffer[..4]); break;
            case TagDouble: BinaryPrimitives.WriteDoubleBigEndian(buffer, (double)value); s.Write(buffer[..8]); break;
            case TagByteArray:
            {
                var bytes = (byte[])value;
                WriteInt(s, bytes.Length);
                s.Write(bytes);
                break;
            }
            case TagString: WriteString(s, (string)value); break;
            case TagList:
            {
                var list = (NbtList)value;
                s.WriteByte(list.Items.Count == 0 && list.Type == TagEnd ? TagEnd : list.Type);
                WriteInt(s, list.Items.Count);
                foreach (var item in list.Items)
                    WritePayload(s, list.Type, item);
                break;
            }
            case TagCompound:
            {
                foreach (var (name, child, childValue) in ((Compound)value).Tags)
                    WriteNamed(s, child, name, childValue);
                s.WriteByte(TagEnd);
                break;
            }
            case TagIntArray:
            {
                var values = (int[])value;
                WriteInt(s, values.Length);
                foreach (var v in values)
                    WriteInt(s, v);
                break;
            }
            case TagLongArray:
            {
                var values = (long[])value;
                WriteInt(s, values.Length);
                foreach (var v in values)
                {
                    BinaryPrimitives.WriteInt64BigEndian(buffer, v);
                    s.Write(buffer[..8]);
                }

                break;
            }
        }
    }

    private static void WriteInt(Stream s, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        s.Write(buffer);
    }

    private static void WriteString(Stream s, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)Math.Min(bytes.Length, ushort.MaxValue));
        s.Write(length);
        s.Write(bytes.AsSpan(0, Math.Min(bytes.Length, ushort.MaxValue)));
    }
}
