using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using SilverPinesCoop.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SilverPinesCoop.Sync
{
    /// <summary>Sprite name hash -> loaded Sprite. Sprites the local player draws are recorded directly (that copy
    /// wins); everything else comes from an index of all loaded sprites, built on the first lookup after a room change
    /// and rebuilt soon after a name we have not seen (a remote sprint / attack frame the local player has not drawn
    /// yet), then only every <see cref="IdleRescan"/> seconds while some stay missing (not loaded on this side).</summary>
    internal sealed class SpriteCatalog
    {
        private const float MissRescan = 0.5f;
        private const float IdleRescan = 10f;
        private static Il2CppSystem.Type s_spriteType;
        private readonly Dictionary<uint, Sprite> _byHash = new();
        private readonly Dictionary<int, uint> _hashByInstance = new();
        private readonly HashSet<uint> _missing = new();
        private bool _indexed;
        private bool _newMissing;
        private float _lastScan = -100f;

        public uint Remember(Sprite sprite)
        {
            var id = sprite.GetInstanceID();
            if (!_hashByInstance.TryGetValue(id, out var hash))
            {
                hash = Packets.Hash(sprite.name);
                _hashByInstance[id] = hash;
            }
            _byHash[hash] = sprite;
            _missing.Remove(hash);
            return hash;
        }

        public Sprite Get(uint hash, float now)
        {
            if (_byHash.TryGetValue(hash, out var sprite))
            {
                if (sprite != null)
                    return sprite;
                _byHash.Remove(hash);   // unloaded with its level
            }
            if (_missing.Add(hash))
                _newMissing = true;
            var wait = !_indexed ? 0f : _newMissing ? MissRescan : IdleRescan;
            if (now - _lastScan < wait)
                return null;
            IndexAll(now);
            return _byHash.TryGetValue(hash, out sprite) && sprite != null ? sprite : null;
        }

        /// <summary>A new room brings new assets: index again on the next miss.</summary>
        public void Invalidate() => _indexed = false;

        private void IndexAll(float now)
        {
            _lastScan = now;
            _indexed = true;
            _newMissing = false;
            s_spriteType ??= Il2CppType.Of<Sprite>();
            var total = 0;
            foreach (var obj in Resources.FindObjectsOfTypeAll(s_spriteType))
            {
                var sprite = obj?.TryCast<Sprite>();
                if (sprite == null)
                    continue;
                total++;
                var hash = Packets.Hash(sprite.name);
                if (!_byHash.ContainsKey(hash))
                    _byHash[hash] = sprite;
                _missing.Remove(hash);
            }
            Plugin.L.LogDebug($"Sprite index: {total} loaded sprites, {_missing.Count} names not loaded on this side");
        }
    }

    /// <summary>How each part of the local player is rendered (material, sorting layer, GameObject layer, rendering
    /// layers), per renderer path. Both players share the prefab, so a puppet part copies these from the same path and
    /// gets the same shader, lighting and camera layer as the original.</summary>
    internal sealed class MaterialCatalog
    {
        public struct Entry
        {
            public Material Material;
            public int SortingLayer;
            public int Layer;
            public uint RenderingLayerMask;
        }

        private readonly Dictionary<uint, Entry> _byPath = new();
        public uint BodyPath { get; set; }

        public void Remember(uint path, SpriteRenderer renderer)
        {
            if (_byPath.ContainsKey(path))
                return;
            _byPath[path] = new Entry
            {
                Material = renderer.sharedMaterial,
                SortingLayer = renderer.sortingLayerID,
                Layer = renderer.gameObject.layer,
                RenderingLayerMask = renderer.renderingLayerMask,
            };
        }

        public bool TryGet(uint path, out Entry entry) =>
            (_byPath.TryGetValue(path, out entry) || _byPath.TryGetValue(BodyPath, out entry)) && entry.Material != null;
    }

    /// <summary>Finds the local player (GlobalReferences.Anchors.Gameplay.PlayerAnchor) and the current room
    /// (GlobalReferences.Anchors.Generic.ActiveLevelMetadata), and records what the player's SpriteRenderers draw
    /// into a <see cref="PlayerSnapshot"/>.</summary>
    internal sealed class LocalPlayer
    {
        public const string BodyPath = "Sprite";   // PlayerCharacter/Sprite: the body (the other parts are arms, head, item, weapons)

        private static Il2CppSystem.Type s_spriteRendererType;
        private readonly Dictionary<int, uint> _pathByRenderer = new();
        private readonly SpriteCatalog _sprites;
        private readonly MaterialCatalog _materials;

        public LocalPlayer(SpriteCatalog sprites, MaterialCatalog materials)
        {
            _sprites = sprites;
            _materials = materials;
            _materials.BodyPath = Packets.Hash(BodyPath);
        }

        /// <summary>The level ("room") the game has active: LevelMetadata.SceneFileName. Levels are Addressables scenes
        /// loaded next to the persistent utility scenes, so the active Unity scene is only a fallback.</summary>
        public static string CurrentRoom()
        {
            try
            {
                // GlobalReferences.Instance loads the asset synchronously when it is not loaded yet: never trigger that
                if (GlobalReferences.Loaded)
                {
                    var level = GlobalReferences.Instance.Anchors?.Generic?.ActiveLevelMetadata?.Item;
                    if (level != null)
                    {
                        var file = level.SceneFileName;
                        return string.IsNullOrEmpty(file) ? level.name : file;
                    }
                }
            }
            catch (Exception)
            {
                // fall through
            }
            var scene = SceneManager.GetActiveScene();
            return scene.IsValid() ? "scene:" + scene.name : "";
        }

        public static GameObject Find()
        {
            try
            {
                if (!GlobalReferences.Loaded)
                    return null;
                var go = GlobalReferences.Instance.Anchors?.Gameplay?.PlayerAnchor?.Item;
                return go != null && go.activeInHierarchy ? go : null;
            }
            catch (Exception)
            {
                return null;   // anchors not set up yet (main menu, loading screens)
            }
        }

        /// <summary>Fills <paramref name="snapshot"/>; false (and HasPlayer = false) when there is no player.</summary>
        public bool Capture(PlayerSnapshot snapshot, uint roomHash)
        {
            snapshot.RoomHash = roomHash;
            snapshot.Renderers.Clear();
            var player = Find();
            snapshot.HasPlayer = player != null;
            if (player == null)
                return false;
            var root = player.transform;
            var rootPosition = root.position;
            snapshot.Position = rootPosition;
            s_spriteRendererType ??= Il2CppType.Of<SpriteRenderer>();
            foreach (var component in player.GetComponentsInChildren(s_spriteRendererType, false))
            {
                var renderer = component?.TryCast<SpriteRenderer>();
                if (renderer == null || !renderer.enabled)
                    continue;
                var sprite = renderer.sprite;
                if (sprite == null)
                    continue;
                var t = renderer.transform;
                var path = PathHash(renderer, root);
                _materials.Remember(path, renderer);
                Decompose(t.localToWorldMatrix, out var rotation, out var scale);
                snapshot.Renderers.Add(new RendererState
                {
                    PathHash = path,
                    SpriteHash = _sprites.Remember(sprite),
                    Offset = t.position - rootPosition,
                    RotationZ = rotation,
                    Scale = scale,
                    SortingOrder = (short)Mathf.Clamp(renderer.sortingOrder, short.MinValue, short.MaxValue),
                    FlipX = renderer.flipX,
                    FlipY = renderer.flipY,
                    Color = renderer.color,
                });
                if (snapshot.Renderers.Count >= PlayerSnapshot.MaxRenderers)
                    break;
            }
            return true;
        }

        /// <summary>The 2D part of a world matrix as R(rotation) * S(scale.x, scale.y), scale.x &gt; 0. Mirroring
        /// parents (the player's Flip / AnimRoot_Flip) end up as a negative scale.y plus 180 degrees, which draws the
        /// same; transform.eulerAngles + lossyScale would get rotated parts under a mirrored parent wrong.</summary>
        private static void Decompose(Matrix4x4 m, out float rotation, out Vector2 scale)
        {
            float a = m.m00, b = m.m01, c = m.m10, d = m.m11;
            var sx = Mathf.Sqrt(a * a + c * c);
            if (sx < 1e-6f)
            {
                rotation = 0f;
                scale = new Vector2(0f, d);
                return;
            }
            var phi = Mathf.Atan2(c, a);
            rotation = phi * Mathf.Rad2Deg;
            scale = new Vector2(sx, -b * Mathf.Sin(phi) + d * Mathf.Cos(phi));
        }

        private uint PathHash(SpriteRenderer renderer, Transform root)
        {
            var id = renderer.GetInstanceID();
            if (_pathByRenderer.TryGetValue(id, out var hash))
                return hash;
            var rootId = root.GetInstanceID();
            var names = new List<string>();
            for (var t = renderer.transform; t != null && t.GetInstanceID() != rootId; t = t.parent)
                names.Add(t.name);
            names.Reverse();
            hash = Packets.Hash(string.Join("/", names));
            _pathByRenderer[id] = hash;
            return hash;
        }
    }
}
