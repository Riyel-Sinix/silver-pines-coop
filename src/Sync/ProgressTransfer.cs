using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace SilverPinesCoop.Sync
{
    /// <summary>
    /// Wire format of a full progress transfer: the game's save JSON, gzip-compressed and split into reliable chunks.
    /// <c>ProgressBegin</c>: u32 transfer id, u32 compressed length, u32 json length, u16 chunk count, 32-byte SHA-256 of
    /// the compressed bytes. <c>ProgressChunk</c>: u32 transfer id, u16 index, u16 length, bytes. Chunks arrive in order on
    /// the reliable channel; anything inconsistent drops the transfer. No Unity types, so the test harness runs it.
    /// </summary>
    internal static class ProgressTransfer
    {
        public const int ChunkSize = 48 * 1024;
        public const int MaxCompressed = 8 * 1024 * 1024;
        public const int MaxJson = 64 * 1024 * 1024;

        public sealed class Outgoing
        {
            public uint Id;
            public int JsonLength;
            public byte[] Compressed;
            public byte[] Hash;
            public int Chunks => (Compressed.Length + ChunkSize - 1) / ChunkSize;

            public void WriteBegin(BinaryWriter w)
            {
                w.Write(Id);
                w.Write((uint)Compressed.Length);
                w.Write((uint)JsonLength);
                w.Write((ushort)Chunks);
                w.Write(Hash);
            }

            public void WriteChunk(BinaryWriter w, int index)
            {
                var offset = index * ChunkSize;
                var length = Math.Min(ChunkSize, Compressed.Length - offset);
                w.Write(Id);
                w.Write((ushort)index);
                w.Write((ushort)length);
                w.Write(Compressed, offset, length);
            }
        }

        public static Outgoing Pack(uint id, string json)
        {
            var raw = Encoding.UTF8.GetBytes(json);
            if (raw.Length > MaxJson)
                throw new InvalidDataException("进度数据过大");
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true))
                gzip.Write(raw, 0, raw.Length);
            var compressed = output.ToArray();
            if (compressed.Length > MaxCompressed || (compressed.Length + ChunkSize - 1) / ChunkSize > ushort.MaxValue)
                throw new InvalidDataException("进度数据压缩后仍过大");
            return new Outgoing { Id = id, JsonLength = raw.Length, Compressed = compressed, Hash = SHA256.HashData(compressed) };
        }

        /// <summary>Collects one transfer from one sender.</summary>
        public sealed class Incoming
        {
            public uint Id { get; private set; }
            public ulong Sender { get; private set; }
            private byte[] _buffer;
            private byte[] _hash;
            private int _jsonLength;
            private int _chunks;
            private int _next;
            private int _filled;

            public static Incoming Begin(ulong sender, BinaryReader r)
            {
                var id = r.ReadUInt32();
                var compressed = r.ReadUInt32();
                var json = r.ReadUInt32();
                var chunks = r.ReadUInt16();
                var hash = r.ReadBytes(32);
                if (compressed == 0 || compressed > MaxCompressed || json == 0 || json > MaxJson || hash.Length != 32 ||
                    chunks != (compressed + ChunkSize - 1) / ChunkSize)
                    throw new InvalidDataException("进度传输头无效");
                return new Incoming
                {
                    Id = id, Sender = sender, _buffer = new byte[compressed], _hash = hash, _jsonLength = (int)json,
                    _chunks = chunks,
                };
            }

            /// <summary>True once the last chunk arrived.</summary>
            public bool Add(ulong sender, BinaryReader r)
            {
                var id = r.ReadUInt32();
                var index = r.ReadUInt16();
                var length = r.ReadUInt16();
                if (sender != Sender || id != Id)
                    throw new InvalidDataException("进度分块不属于当前传输");
                if (index != _next || length == 0 || length > ChunkSize || _filled + length > _buffer.Length ||
                    (index < _chunks - 1 && length != ChunkSize))
                    throw new InvalidDataException("进度分块顺序或长度错误");
                var read = r.Read(_buffer, _filled, length);
                if (read != length)
                    throw new InvalidDataException("进度分块不完整");
                _filled += length;
                _next++;
                return _next == _chunks;
            }

            public string Finish()
            {
                if (_next != _chunks || _filled != _buffer.Length)
                    throw new InvalidDataException("进度数据不完整");
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(_buffer), _hash))
                    throw new InvalidDataException("进度数据校验失败");
                using var input = new GZipStream(new MemoryStream(_buffer), CompressionMode.Decompress);
                var raw = new byte[_jsonLength];
                var total = 0;
                int n;
                while (total < raw.Length && (n = input.Read(raw, total, raw.Length - total)) > 0)
                    total += n;
                if (total != raw.Length || input.ReadByte() != -1)
                    throw new InvalidDataException("进度数据长度不符");
                return Encoding.UTF8.GetString(raw);
            }
        }

        /// <summary>"[prefix:]guid:Type" (PersistentDataIdentifier.UpdateFromDatastore): the GUID and component type.</summary>
        public static bool SplitKey(string key, out string guid, out string type)
        {
            guid = type = null;
            if (string.IsNullOrEmpty(key))
                return false;
            var last = key.LastIndexOf(':');
            if (last <= 0 || last == key.Length - 1)
                return false;
            type = key[(last + 1)..];
            var before = key.LastIndexOf(':', last - 1);
            guid = key[(before + 1)..last];
            return guid.Length > 0;
        }

        /// <summary>Keys of components that record "this player took it".</summary>
        public static readonly HashSet<string> PickupTypes = new()
        {
            "ItemPickup", "InteractableLootContainerCommand", "InteractablePickupMoneyCommand", "ShootableLootContainer",
            "LorePickup",
        };
    }
}
