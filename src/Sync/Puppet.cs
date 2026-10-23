using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SilverPinesCoop.Sync
{
    /// <summary>
    /// The 2P stand-in for one remote player: a root GameObject (DontDestroyOnLoad) with one SpriteRenderer per part the
    /// remote player drew. Snapshots are kept in a small buffer and rendered <c>delay</c> seconds in the past: the root
    /// position is interpolated between the two snapshots around that time, the parts come from the older one. Shown only
    /// while both players are in the same room.
    /// </summary>
    internal sealed class Puppet
    {
        private const int BufferSize = 16;
        private static Il2CppSystem.Type s_spriteRendererType;

        public readonly ulong PeerId;
        public uint RoomHash { get; private set; }
        public bool HasSnapshot => _count > 0;

        private readonly GameObject _root;
        private readonly Dictionary<uint, SpriteRenderer> _parts = new();
        private readonly HashSet<uint> _drawn = new();
        private readonly PlayerSnapshot[] _ring = new PlayerSnapshot[BufferSize];
        private int _head;   // index of the newest snapshot
        private int _count;
        private uint _lastSequence;

        public Puppet(ulong peerId, string label)
        {
            PeerId = peerId;
            for (var i = 0; i < BufferSize; i++)
                _ring[i] = new PlayerSnapshot();
            _root = new GameObject($"Coop2P_{label}");
            Object.DontDestroyOnLoad(_root);
            _root.SetActive(false);
        }

        /// <summary>Keeps a copy of <paramref name="snapshot"/>; late or duplicate packets (unreliable channel) are dropped.</summary>
        public void Push(PlayerSnapshot snapshot)
        {
            if (_count > 0 && unchecked((int)(snapshot.Sequence - _lastSequence)) <= 0)
                return;
            _lastSequence = snapshot.Sequence;
            _head = (_head + 1) % BufferSize;
            _ring[_head].CopyFrom(snapshot);
            if (_count < BufferSize)
                _count++;
            RoomHash = snapshot.RoomHash;
        }

        public void Render(float now, float delay, uint localRoom, bool localPlayerPresent, Vector3 extraOffset,
                           SpriteCatalog sprites, MaterialCatalog materials)
        {
            if (_root == null)
                return;
            if (_count == 0)
            {
                SetVisible(false);
                return;
            }
            Pick(now - delay, out var from, out var to, out var t);
            var visible = from.HasPlayer && from.RoomHash == localRoom && localPlayerPresent;
            SetVisible(visible);
            if (!visible)
                return;

            var position = to == null ? from.Position : Vector3.Lerp(from.Position, to.Position, t);
            position += extraOffset;
            _root.transform.position = position;

            _drawn.Clear();
            foreach (var part in from.Renderers)
            {
                var renderer = Part(part.PathHash, materials);
                // a frame we cannot resolve yet keeps the part's previous frame instead of blinking it out
                var sprite = sprites.Get(part.SpriteHash, now) ?? renderer.sprite;
                if (sprite == null)
                {
                    if (renderer.enabled)
                        renderer.enabled = false;
                    continue;
                }
                _drawn.Add(part.PathHash);
                if (!renderer.enabled)
                    renderer.enabled = true;
                renderer.sprite = sprite;
                renderer.flipX = part.FlipX;
                renderer.flipY = part.FlipY;
                renderer.sortingOrder = part.SortingOrder;
                renderer.color = part.Color;
                var tr = renderer.transform;
                tr.position = position + part.Offset;
                tr.rotation = Quaternion.Euler(0f, 0f, part.RotationZ);
                tr.localScale = new Vector3(part.Scale.x, part.Scale.y, 1f);
            }
            foreach (var pair in _parts)
                if (!_drawn.Contains(pair.Key) && pair.Value.enabled)
                    pair.Value.enabled = false;
        }

        public void Destroy()
        {
            if (_root != null)
                Object.Destroy(_root);
            _parts.Clear();
        }

        private void SetVisible(bool visible)
        {
            if (_root.activeSelf != visible)
                _root.SetActive(visible);
        }

        private SpriteRenderer Part(uint path, MaterialCatalog materials)
        {
            if (_parts.TryGetValue(path, out var renderer) && renderer != null)
                return renderer;
            var go = new GameObject($"part_{path:x8}");
            go.transform.SetParent(_root.transform, false);
            s_spriteRendererType ??= Il2CppType.Of<SpriteRenderer>();
            renderer = go.AddComponent(s_spriteRendererType).Cast<SpriteRenderer>();
            if (materials.TryGet(path, out var look))
            {
                renderer.sharedMaterial = look.Material;
                renderer.sortingLayerID = look.SortingLayer;
                renderer.renderingLayerMask = look.RenderingLayerMask;
                go.layer = look.Layer;
            }
            _parts[path] = renderer;
            return renderer;
        }

        /// <summary>The newest snapshot received at or before <paramref name="renderTime"/> and the one after it
        /// (null when <paramref name="renderTime"/> is past the newest), with the lerp factor between them.</summary>
        private void Pick(float renderTime, out PlayerSnapshot from, out PlayerSnapshot to, out float t)
        {
            from = _ring[(_head - (_count - 1) + BufferSize) % BufferSize];   // oldest
            to = null;
            t = 0f;
            for (var i = 0; i < _count; i++)
            {
                var s = _ring[(_head - i + BufferSize) % BufferSize];
                if (s.ReceivedAt > renderTime)
                {
                    to = s;
                    continue;
                }
                from = s;
                if (to != null)
                {
                    var span = to.ReceivedAt - from.ReceivedAt;
                    t = span > 1e-4f ? Mathf.Clamp01((renderTime - from.ReceivedAt) / span) : 1f;
                }
                return;
            }
            // every snapshot is newer than renderTime: hold the oldest
            to = null;
        }
    }
}
