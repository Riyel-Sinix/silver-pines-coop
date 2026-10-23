using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SilverPinesCoop.Sync
{
    /// <summary>One SpriteRenderer of the player as it was drawn: which sprite, where relative to the player root, how
    /// rotated / scaled / flipped and in which order. Syncing what is drawn (instead of Animator states) reproduces
    /// the PlayMaker / anim-node driven sprites exactly, and the frog mod's sprites come along by themselves.</summary>
    internal struct RendererState
    {
        public uint PathHash;        // transform path below the player root, e.g. "AnimRoot/.../Arms/Arm Front"
        public uint SpriteHash;      // sprite name
        public Vector3 Offset;       // world offset from the player root
        public float RotationZ;      // world euler z, degrees
        public Vector2 Scale;        // lossy scale (negative x = facing the other way)
        public short SortingOrder;
        public bool FlipX;
        public bool FlipY;
        public Color32 Color;
    }

    /// <summary>The PlayerState packet body (channel 0, unreliable): little-endian, halves for the per-renderer
    /// offsets / rotation / scale (27 bytes per renderer).</summary>
    internal sealed class PlayerSnapshot
    {
        public const int MaxRenderers = 32;

        public uint Sequence;
        public uint RoomHash;
        public bool HasPlayer;
        public Vector3 Position;
        public float ReceivedAt;     // local time, set by the receiver
        public readonly List<RendererState> Renderers = new(MaxRenderers);

        public void Write(BinaryWriter w)
        {
            w.Write(Sequence);
            w.Write(RoomHash);
            w.Write((byte)(HasPlayer ? 1 : 0));
            w.Write(Position.x);
            w.Write(Position.y);
            w.Write(Position.z);
            var count = Math.Min(Renderers.Count, MaxRenderers);
            w.Write((byte)count);
            for (var i = 0; i < count; i++)
            {
                var r = Renderers[i];
                w.Write(r.PathHash);
                w.Write(r.SpriteHash);
                w.Write((Half)r.Offset.x);
                w.Write((Half)r.Offset.y);
                w.Write((Half)r.Offset.z);
                w.Write((Half)r.RotationZ);
                w.Write((Half)r.Scale.x);
                w.Write((Half)r.Scale.y);
                w.Write(r.SortingOrder);
                w.Write((byte)((r.FlipX ? 1 : 0) | (r.FlipY ? 2 : 0)));
                w.Write(r.Color.r);
                w.Write(r.Color.g);
                w.Write(r.Color.b);
                w.Write(r.Color.a);
            }
        }

        public void Read(BinaryReader r)
        {
            Sequence = r.ReadUInt32();
            RoomHash = r.ReadUInt32();
            HasPlayer = (r.ReadByte() & 1) != 0;
            Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var count = Math.Min((int)r.ReadByte(), MaxRenderers);
            Renderers.Clear();
            for (var i = 0; i < count; i++)
            {
                var s = new RendererState
                {
                    PathHash = r.ReadUInt32(),
                    SpriteHash = r.ReadUInt32(),
                    Offset = new Vector3((float)r.ReadHalf(), (float)r.ReadHalf(), (float)r.ReadHalf()),
                    RotationZ = (float)r.ReadHalf(),
                    Scale = new Vector2((float)r.ReadHalf(), (float)r.ReadHalf()),
                    SortingOrder = r.ReadInt16(),
                };
                var flags = r.ReadByte();
                s.FlipX = (flags & 1) != 0;
                s.FlipY = (flags & 2) != 0;
                s.Color = new Color32(r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte());
                Renderers.Add(s);
            }
        }

        public void CopyFrom(PlayerSnapshot other)
        {
            Sequence = other.Sequence;
            RoomHash = other.RoomHash;
            HasPlayer = other.HasPlayer;
            Position = other.Position;
            ReceivedAt = other.ReceivedAt;
            Renderers.Clear();
            Renderers.AddRange(other.Renderers);
        }
    }
}
