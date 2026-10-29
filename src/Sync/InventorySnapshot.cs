using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SilverPinesCoop.Sync
{
    internal sealed class InventoryItemRecord
    {
        public string Image, Namespace, Type, Guid, AmmoGuid;
        public byte[] Fields;
        public List<InventoryItemRecord> Children = new();
    }

    internal static class InventorySnapshotCodec
    {
        public const int MaxItems = 256;
        public const int MaxFieldBytes = 2048;
        public const int MaxSnapshotBytes = 48 * 1024;
        public const int MaxDepth = 8;

        public static byte[] Encode(string key, IList<InventoryItemRecord> records)
        {
            if (records == null || records.Count > MaxItems)
                throw new InvalidDataException("Invalid shared inventory item count");
            using var stream = new MemoryStream();
            var totalItems = 0;
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(key ?? "");
                writer.Write((ushort)records.Count);
                foreach (var item in records)
                    WriteItem(writer, stream, item, 0, ref totalItems);
            }
            return stream.ToArray();
        }

        private static void WriteItem(BinaryWriter writer, MemoryStream stream, InventoryItemRecord item, int depth,
                                      ref int totalItems)
        {
            if (depth > MaxDepth || ++totalItems > MaxItems || item == null || string.IsNullOrEmpty(item.Guid) ||
                item.Fields == null || item.Fields.Length > MaxFieldBytes || item.Children == null || item.Children.Count > MaxItems)
                throw new InvalidDataException("Invalid shared inventory item");
            writer.Write(item.Image ?? "");
            writer.Write(item.Namespace ?? "");
            writer.Write(item.Type ?? "");
            writer.Write(item.Guid);
            writer.Write(item.AmmoGuid ?? "");
            writer.Write((ushort)item.Fields.Length);
            writer.Write(item.Fields);
            writer.Write((ushort)item.Children.Count);
            if (stream.Length > MaxSnapshotBytes)
                throw new InvalidDataException("Shared inventory snapshot is too large");
            foreach (var child in item.Children)
                WriteItem(writer, stream, child, depth + 1, ref totalItems);
        }

        public static bool TryDecode(BinaryReader reader, out string key, out List<InventoryItemRecord> records)
        {
            key = "";
            records = null;
            try
            {
                key = reader.ReadString();
                var count = reader.ReadUInt16();
                if (count > MaxItems)
                    return false;
                var result = new List<InventoryItemRecord>(count);
                var totalBytes = 0;
                var totalItems = 0;
                for (var i = 0; i < count; i++)
                    result.Add(ReadItem(reader, 0, ref totalItems, ref totalBytes));
                if (reader.BaseStream.Position > MaxSnapshotBytes)
                    return false;
                records = result;
                return true;
            }
            catch (EndOfStreamException)
            {
                return false;
            }
            catch (InvalidDataException)
            {
                return false;
            }
            catch (FormatException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        private static InventoryItemRecord ReadItem(BinaryReader reader, int depth, ref int totalItems, ref int totalBytes)
        {
            if (depth > MaxDepth || ++totalItems > MaxItems)
                throw new InvalidDataException("Invalid shared inventory nesting");
            var item = new InventoryItemRecord
            {
                Image = reader.ReadString(),
                Namespace = reader.ReadString(),
                Type = reader.ReadString(),
                Guid = reader.ReadString(),
                AmmoGuid = reader.ReadString(),
            };
            var length = reader.ReadUInt16();
            if (length > MaxFieldBytes)
                throw new InvalidDataException("Shared inventory item is too large");
            item.Fields = reader.ReadBytes(length);
            totalBytes += item.Fields.Length;
            if (item.Fields.Length != length || string.IsNullOrEmpty(item.Guid) || totalBytes > MaxSnapshotBytes)
                throw new InvalidDataException("Invalid shared inventory item data");
            var children = reader.ReadUInt16();
            if (children > MaxItems)
                throw new InvalidDataException("Too many nested shared inventory items");
            item.Children = new List<InventoryItemRecord>(children);
            for (var i = 0; i < children; i++)
                item.Children.Add(ReadItem(reader, depth + 1, ref totalItems, ref totalBytes));
            return item;
        }
    }
}
