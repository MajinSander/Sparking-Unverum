using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SparkingUnverum
{
    // A game file inside a mod: its path in the game and a hash of its content when the format has one
    public record AssetFile(string Path, string Hash);

    // Reads the list of files of a mod package without unpacking it: the directory index of IoStore
    // containers (.utoc) and the index of classic .pak files. Encrypted indexes can't be read
    public static class AssetIndex
    {
        private static readonly byte[] TocMagic = Encoding.ASCII.GetBytes("-==--==--==--==-");
        private const uint PakMagic = 0x5A6F12E1;
        private const uint None = 0xFFFFFFFF;

        // null when the index is encrypted or the file isn't a package
        public static List<AssetFile> Read(string file)
        {
            try
            {
                return Path.GetExtension(file).ToLowerInvariant() switch
                {
                    ".utoc" => ReadToc(file),
                    ".pak" => ReadPak(file),
                    _ => null
                };
            }
            catch
            {
                return null;
            }
        }

        // ---------- IoStore (.utoc) ----------

        private static List<AssetFile> ReadToc(string file)
        {
            var b = File.ReadAllBytes(file);
            if (b.Length < 144 || !b.AsSpan(0, 16).SequenceEqual(TocMagic))
                return null;
            var version = b[16];
            var headerSize = BitConverter.ToUInt32(b, 20);
            var entries = BitConverter.ToUInt32(b, 24);
            var blocks = BitConverter.ToUInt32(b, 28);
            var blockSize = BitConverter.ToUInt32(b, 32);
            var methods = BitConverter.ToUInt32(b, 36);
            var methodLength = BitConverter.ToUInt32(b, 40);
            var directorySize = BitConverter.ToUInt32(b, 48);
            var flags = b[80];
            var seeds = BitConverter.ToUInt32(b, 84);
            var withoutPerfectHash = BitConverter.ToUInt32(b, 96);
            const byte Encrypted = 2, Signed = 4, Indexed = 8;
            if ((flags & Encrypted) != 0)
                return null;
            if ((flags & Indexed) == 0 || directorySize == 0 || version < 2)
                return new List<AssetFile>();

            long o = headerSize + entries * 12L + entries * 10L;
            if (version >= 4)
                o += seeds * 4L;
            if (version >= 5)
                o += withoutPerfectHash * 4L;
            o += (long)blocks * blockSize + (long)methods * methodLength;
            if ((flags & Signed) != 0)
            {
                var hashSize = BitConverter.ToInt32(b, (int)o);
                o += 4 + hashSize * 2L + blocks * 20L;
            }
            var directory = (int)o;
            var metas = directory + (int)directorySize;
            // Each chunk has a hash of its content after the directory index (FIoHash from version 8)
            var metaSize = version >= 8 ? 24 : 33;

            var p = directory;
            var mount = ReadString(b, ref p);
            var dirs = new uint[BitConverter.ToInt32(b, p), 4];
            p += 4;
            for (int i = 0; i < dirs.GetLength(0); i++, p += 16)
                for (int j = 0; j < 4; j++)
                    dirs[i, j] = BitConverter.ToUInt32(b, p + j * 4);
            var files = new uint[BitConverter.ToInt32(b, p), 3];
            p += 4;
            for (int i = 0; i < files.GetLength(0); i++, p += 12)
                for (int j = 0; j < 3; j++)
                    files[i, j] = BitConverter.ToUInt32(b, p + j * 4);
            var strings = new string[BitConverter.ToInt32(b, p)];
            p += 4;
            for (int i = 0; i < strings.Length; i++)
                strings[i] = ReadString(b, ref p);

            var result = new List<AssetFile>();
            if (dirs.GetLength(0) == 0)
                return result;
            // Directory entries: name, first child, next sibling, first file. Files: name, next file, chunk
            var pending = new Stack<(uint Dir, string Prefix)>();
            pending.Push((0, mount));
            while (pending.Count > 0)
            {
                var (dir, prefix) = pending.Pop();
                var here = dirs[dir, 0] == None ? prefix : prefix + strings[dirs[dir, 0]] + "/";
                for (var f = dirs[dir, 3]; f != None; f = files[f, 1])
                {
                    var chunk = files[f, 2];
                    var hashAt = metas + (long)chunk * metaSize;
                    var hash = hashAt + 20 <= b.Length ? Convert.ToHexString(b, (int)hashAt, 20) : null;
                    result.Add(new AssetFile(Normalize(here + strings[files[f, 0]]), hash));
                }
                for (var child = dirs[dir, 1]; child != None; child = dirs[child, 2])
                    pending.Push((child, here));
            }
            return result;
        }

        // ---------- Classic .pak ----------

        private static List<AssetFile> ReadPak(string file)
        {
            using var stream = File.OpenRead(file);
            using var reader = new BinaryReader(stream);
            // The footer is at the end and its size depends on the version: find the magic number
            // (221: 8 and 10+, 222: 9, 189: some 8, 45: 4 to 7, 44: up to 3)
            foreach (var footer in new[] { 221, 222, 189, 45, 44 })
            {
                if (stream.Length < footer)
                    continue;
                // Footers with an encrypted flag have it right before the magic
                stream.Position = stream.Length - footer + (footer >= 189 ? 17 : footer == 45 ? 1 : 0);
                if (reader.ReadUInt32() != PakMagic)
                    continue;
                var version = reader.ReadInt32();
                var indexOffset = reader.ReadInt64();
                var indexSize = reader.ReadInt64();
                stream.Position = stream.Length - footer + (footer >= 189 ? 16 : 0);
                if (footer >= 45 && reader.ReadByte() != 0)
                    return null;
                if (indexOffset <= 0 || indexSize <= 0 || indexOffset + indexSize > stream.Length)
                    return null;
                stream.Position = indexOffset;
                var index = reader.ReadBytes((int)indexSize);
                return version >= 10 ? ReadPakIndexV10(index, reader, stream) : ReadPakIndexLegacy(index, version);
            }
            return null;
        }

        // Up to version 9: mount point, then every entry with its file name and its SHA1
        private static List<AssetFile> ReadPakIndexLegacy(byte[] b, int version)
        {
            var p = 0;
            var mount = ReadString(b, ref p);
            var count = BitConverter.ToInt32(b, p);
            p += 4;
            var result = new List<AssetFile>(count);
            for (int i = 0; i < count; i++)
            {
                var name = ReadString(b, ref p);
                p += 8 + 8 + 8; // offset, size, uncompressed size
                var compression = BitConverter.ToUInt32(b, p);
                p += 4;
                if (version <= 1)
                    p += 8; // timestamp
                var hash = Convert.ToHexString(b, p, 20);
                p += 20;
                if (version >= 3)
                {
                    if (compression != 0)
                    {
                        var compressedBlocks = BitConverter.ToInt32(b, p);
                        p += 4 + compressedBlocks * 16;
                    }
                    p += 1 + 4; // flags, block size
                }
                result.Add(new AssetFile(Normalize(mount + name), hash));
            }
            return result;
        }

        // Version 10 and later: the names are in the full directory index, stored elsewhere in the file
        private static List<AssetFile> ReadPakIndexV10(byte[] b, BinaryReader reader, Stream stream)
        {
            var p = 0;
            var mount = ReadString(b, ref p);
            p += 4 + 8; // entry count, path hash seed
            if (BitConverter.ToInt32(b, p) != 0)
                p += 4 + 8 + 8 + 20;
            else
                p += 4;
            if (BitConverter.ToInt32(b, p) == 0)
                return new List<AssetFile>();
            p += 4;
            var directoryOffset = BitConverter.ToInt64(b, p);
            var directorySize = BitConverter.ToInt64(b, p + 8);
            if (directoryOffset <= 0 || directoryOffset + directorySize > stream.Length)
                return null;
            stream.Position = directoryOffset;
            var d = reader.ReadBytes((int)directorySize);
            var q = 0;
            var result = new List<AssetFile>();
            var dirCount = BitConverter.ToInt32(d, q);
            q += 4;
            for (int i = 0; i < dirCount; i++)
            {
                var dir = ReadString(d, ref q);
                var fileCount = BitConverter.ToInt32(d, q);
                q += 4;
                for (int j = 0; j < fileCount; j++)
                {
                    var name = ReadString(d, ref q);
                    q += 4; // offset of the encoded entry, it has no hash
                    result.Add(new AssetFile(Normalize(mount + dir.TrimStart('/') + name), null));
                }
            }
            return result;
        }

        // Unreal FString: length with the final zero, negative for UTF-16
        private static string ReadString(byte[] b, ref int p)
        {
            var length = BitConverter.ToInt32(b, p);
            p += 4;
            if (length == 0)
                return string.Empty;
            string text;
            if (length > 0)
            {
                text = Encoding.Latin1.GetString(b, p, length - 1);
                p += length;
            }
            else
            {
                text = Encoding.Unicode.GetString(b, p, (-length - 1) * 2);
                p += -length * 2;
            }
            return text;
        }

        // "../../../SparkingZERO/Content/SS/x.uasset" -> "SparkingZERO/Content/SS/x.uasset"
        private static string Normalize(string path)
        {
            path = path.Replace('\\', '/');
            while (path.StartsWith("../", StringComparison.Ordinal))
                path = path[3..];
            return path.TrimStart('/');
        }
    }
}
