using System;
using System.IO;
using System.Text;

namespace SilverPinesCoop.Net
{
    /// <summary>First byte of every message. Channel 0 carries only <see cref="PlayerState"/>; everything else is
    /// reliable on channel 1 and goes through <see cref="SteamNetManager.RegisterHandler"/>.</summary>
    public enum PacketType : byte
    {
        Hello = 1,          // ch1: protocol, mod and game version of the sender
        PlayerState = 2,    // ch0: position, room and the rendered sprites of the player (TickRate per second)
        ChangeRoom = 10,    // ch1: the sender is now in another room (level)
        MonsterDamage = 11, // ch1: an enemy lost health on the sender's side (EnemySync)
        PauseState = 12,    // ch1: the sender's player is in the pause menu / a cutscene (PauseSync)
        WorldEntry = 13,    // ch1: a save entry (door, pickup, interactable, ...) changed (WorldSync)
        StoryFlag = 14,     // ch1: a persistent story variable changed (WorldSync)
        CutsceneStart = 15, // ch1: the sender started a timeline cutscene (CutsceneSync)
        SharedInventory = 16, // ch1: shared storage inventory snapshot (WorldSync)
        CheckpointSession = 20,
        CheckpointSaveRequest = 21,
        CheckpointSavePrepare = 22,
        CheckpointSaveAck = 23,
        CheckpointSaveFinish = 24,
        CheckpointLoadRequest = 25,
        CheckpointLoadPrepare = 26,
        CheckpointLoadAck = 27,
        CheckpointLoadFinish = 28,
        CheckpointAbort = 29,
        CheckpointDeath = 30,
        CheckpointSessionAck = 31,
        CheckpointReady = 32,
        CheckpointExecute = 33,
    }

    public static class Packets
    {
        public const ushort ProtocolVersion = 4;
        public const int ChannelUnreliable = 0;
        public const int ChannelReliable = 1;
        public const int MaxMessageSize = 64 * 1024;

        /// <summary>FNV-1a over UTF-8: the same 32-bit id for a sprite / transform path / scene name on every client
        /// (string.GetHashCode is randomized per process).</summary>
        public static uint Hash(string s)
        {
            var h = 2166136261u;
            if (string.IsNullOrEmpty(s))
                return h;
            Span<byte> buffer = s.Length <= 256 ? stackalloc byte[s.Length * 3] : new byte[Encoding.UTF8.GetMaxByteCount(s.Length)];
            var n = Encoding.UTF8.GetBytes(s, buffer);
            for (var i = 0; i < n; i++)
            {
                h ^= buffer[i];
                h *= 16777619u;
            }
            return h;
        }
    }

    /// <summary>Health an enemy lost on the sender's side; MonsterId is the hash of its PersistentDataIdentifier GUID.</summary>
    public readonly struct MonsterDamage
    {
        public readonly uint MonsterId;
        public readonly float Amount;
        public readonly byte DamageType;
        public readonly float HitX;
        public readonly float HitY;

        public MonsterDamage(uint monsterId, float amount, byte damageType, float hitX, float hitY)
        {
            MonsterId = monsterId;
            Amount = amount;
            DamageType = damageType;
            HitX = hitX;
            HitY = hitY;
        }

        public void Write(BinaryWriter w)
        {
            w.Write(MonsterId);
            w.Write(Amount);
            w.Write(DamageType);
            w.Write(HitX);
            w.Write(HitY);
        }

        public static MonsterDamage Read(BinaryReader r) =>
            new(r.ReadUInt32(), r.ReadSingle(), r.ReadByte(), r.ReadSingle(), r.ReadSingle());
    }

    /// <summary>A reusable little-endian writer: the type byte first, then the payload.</summary>
    internal sealed class PacketWriter
    {
        private readonly MemoryStream _stream = new(1024);

        public PacketWriter()
        {
            Writer = new BinaryWriter(_stream, Encoding.UTF8, true);
        }

        public BinaryWriter Writer { get; }
        public byte[] Buffer => _stream.GetBuffer();
        public int Length => (int)_stream.Length;

        public BinaryWriter Begin(PacketType type)
        {
            _stream.SetLength(0);
            Writer.Write((byte)type);
            return Writer;
        }
    }
}
