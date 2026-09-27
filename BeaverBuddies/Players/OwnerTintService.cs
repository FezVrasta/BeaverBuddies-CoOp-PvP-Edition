using System.Collections.Generic;
using Timberborn.Timbermesh;
using Timberborn.AssetSystem;
using System.IO;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.Buildings;
using Timberborn.Coordinates;
using Timberborn.EntitySystem;
using Timberborn.GameDistricts;
using Timberborn.PathSystem;
using Timberborn.Rendering;
using Timberborn.SelectionSystem;
using Timberborn.SingletonSystem;
using UnityEngine;

namespace BeaverBuddies.Players
{
    /**
     * Marks buildings that can be tinted with their owner's color. Added
     * to every building by a blueprint decorator (see OwnerTintConfigurator).
     */
    public class OwnerTint : BaseComponent, IAwakableComponent, IDeletableEntity
    {
        public DistrictCenter DistrictCenter { get; private set; }
        public BlockObject BlockObject { get; private set; }
        public PlacedBy PlacedBy { get; private set; }
        public bool IsPath { get; private set; }
        public Color? AppliedColor { get; set; }
        // A finished district center shows its owner on the faction glyph
        public Color? GlyphColor { get; set; }

        private List<Material> _glyphMaterials;

        /**
         * The materials of the faction glyph overlay, attached to the finished
         * district center the first time it's asked for. The game draws the
         * district center with one merged material, so the glyph gets its own
         * copy on top to be tinted alone.
         */
        public List<Material> GetGlyphMaterials(OwnerTintService service)
        {
            if (_glyphMaterials != null) return _glyphMaterials;
            if (!DistrictCenter || !BlockObject || !BlockObject.IsFinished) return Empty;
            BuildingModel model = GetComponent<BuildingModel>();
            if (!model || !model.FinishedModel) return Empty;
            _glyphMaterials = service.AttachGlyph(model.FinishedModel.transform, GameObject.name);
            return _glyphMaterials;
        }

        private static readonly List<Material> Empty = new();

        private List<(ParticleSystem system, ParticleSystem.MinMaxGradient color)> _flames;
        private List<(Light light, Color color)> _fireLights;

        /**
         * The fire on top of a district center (a template attachment), with
         * its original colors so they can be put back.
         */
        public void GetFire(out List<(ParticleSystem system, ParticleSystem.MinMaxGradient color)> flames,
            out List<(Light light, Color color)> lights)
        {
            if (_flames == null)
            {
                _flames = new();
                _fireLights = new();
                if (DistrictCenter)
                {
                    foreach (Transform child in GameObject.GetComponentsInChildren<Transform>(true))
                    {
                        if (!child.name.StartsWith(OwnerTintService.FireObjectName)) continue;
                        foreach (ParticleSystem system in child.GetComponentsInChildren<ParticleSystem>(true))
                        {
                            _flames.Add((system, system.main.startColor));
                        }
                        foreach (Light light in child.GetComponentsInChildren<Light>(true))
                        {
                            _fireLights.Add((light, light.color));
                        }
                    }
                }
            }
            flames = _flames;
            lights = _fireLights;
            // The fire might not be attached yet, look again next time
            if (_flames.Count == 0 && _fireLights.Count == 0) _flames = null;
        }

        public void Awake()
        {
            DistrictCenter = GetComponent<DistrictCenter>();
            BlockObject = GetComponent<BlockObject>();
            PlacedBy = GetComponent<PlacedBy>();
            IsPath = HasComponent<PathSpec>();
            OwnerTintService.Instance?.Register(this);
        }

        public void DeleteEntity()
        {
            OwnerTintService.Instance?.Unregister(this);
        }
    }

    /**
     * Shows each player's color on their district centers' faction glyphs
     * and fires, and as a faint tint on their paths and unbuilt construction sites, so
     * it's clear who owns what and who is going to build what. It only
     * changes how things look on this machine, never the game.
     */
    public class OwnerTintService : RegisteredSingleton, IUpdatableSingleton
    {
        // Highlights are added on top of the model, so a small fraction of
        // the color is enough (the game's own highlights are around 0.2)
        private const float Strength = 0.2f;
        // The glyph is small, so it takes more of the color to read
        private const float GlyphStrength = 0.7f;
        // The glyph's material on both factions' district centers, used by
        // nothing else on them
        private const string GlyphModelPath = "Buildings/DistrictCenterGlyph/DistrictCenterGlyph";
        private const string GlyphObjectName = "#OwnerGlyph";
        public const string FireObjectName = "DistrictCenterFire";
        // Mixed into the fire's color so it still reads as flames
        private const float FireWhiteness = 0.35f;
        private static readonly int EmissionColorProperty = Shader.PropertyToID("_EmissionColor");
        private const float UpdateSeconds = 1f;
        // Other systems can reset an object's highlights, so reapply now
        // and then even if the owner didn't change
        private const float ReapplySeconds = 5f;

        private readonly Highlighter _highlighter;
        private readonly DistrictCenterRegistry _districtCenterRegistry;

        private readonly HashSet<OwnerTint> _tints = new();
        private readonly List<OwnerTint> _dead = new();
        private float _nextUpdate;
        private float _nextReapply;
        private bool _shown = true;

        public static OwnerTintService Instance => SingletonManager.GetSingleton<OwnerTintService>();

        private readonly IAssetLoader _assetLoader;
        private readonly TimbermeshImporter _timbermeshImporter;

        public OwnerTintService(Highlighter highlighter, DistrictCenterRegistry districtCenterRegistry,
            IAssetLoader assetLoader, TimbermeshImporter timbermeshImporter)
        {
            _highlighter = highlighter;
            _districtCenterRegistry = districtCenterRegistry;
            _assetLoader = assetLoader;
            _timbermeshImporter = timbermeshImporter;
        }

        public List<Material> AttachGlyph(Transform finishedModel, string templateName)
        {
            var materials = new List<Material>();
            string faction = templateName.Contains("IronTeeth") ? "IronTeeth" : "Folktails";
            BinaryData data = _assetLoader.LoadSafe<BinaryData>($"{GlyphModelPath}.{faction}.Model");
            if (!data)
            {
                Plugin.LogWarning($"No district center glyph model for {faction}");
                return materials;
            }
            var glyph = new GameObject(GlyphObjectName);
            glyph.transform.SetParent(finishedModel, false);
            using (var stream = new MemoryStream(data.Bytes))
            {
                _timbermeshImporter.Import(stream, glyph.transform);
            }
            foreach (MeshRenderer renderer in glyph.GetComponentsInChildren<MeshRenderer>(true))
            {
                materials.AddRange(renderer.materials);
            }
            return materials;
        }

        public void Register(OwnerTint tint) => _tints.Add(tint);

        public void Unregister(OwnerTint tint)
        {
            if (_tints.Remove(tint) && tint.AppliedColor.HasValue) _highlighter.UnhighlightSecondary(tint);
        }

        private bool UsesGlyph(OwnerTint tint) => tint.GetGlyphMaterials(this).Count > 0;

        public void UpdateSingleton()
        {
            float now = Time.unscaledTime;
            if (now < _nextUpdate) return;
            _nextUpdate = now + UpdateSeconds;
            bool reapply = now >= _nextReapply;
            if (reapply) _nextReapply = now + ReapplySeconds;

            var ownership = DistrictOwnershipService.Instance;
            bool show = Settings.ShowOwnerColors && ownership != null;
            if (!show)
            {
                if (_shown) ClearAll();
                _shown = false;
                return;
            }
            _shown = true;

            foreach (OwnerTint tint in _tints)
            {
                // Previews and deleted objects don't always get DeleteEntity
                if (!tint) { _dead.Add(tint); continue; }
                Color? color = GetColor(tint, ownership);
                if (UsesGlyph(tint))
                {
                    ApplyGlyph(tint, color);
                    ApplyFire(tint, GetOwnerColor(tint, ownership));
                    continue;
                }
                if (color == tint.AppliedColor && !reapply) continue;
                Apply(tint, color);
            }
            foreach (OwnerTint tint in _dead) _tints.Remove(tint);
            _dead.Clear();
        }

        private static Color? GetOwnerColor(OwnerTint tint, DistrictOwnershipService ownership)
        {
            return tint.DistrictCenter ? ownership.GetPlayerColor(ownership.GetDistrictOwner(tint.DistrictCenter)) : null;
        }

        private static void ApplyFire(OwnerTint tint, Color? ownerColor)
        {
            tint.GetFire(out var flames, out var lights);
            foreach (var (system, original) in flames)
            {
                if (!system) continue;
                ParticleSystem.MainModule main = system.main;
                if (ownerColor.HasValue)
                {
                    Color c = Color.Lerp(ownerColor.Value, Color.white, FireWhiteness);
                    c.a = original.color.a;
                    main.startColor = c;
                }
                else
                {
                    main.startColor = original;
                }
            }
            foreach (var (light, original) in lights)
            {
                if (light) light.color = ownerColor ?? original;
            }
        }

        private Color? GetColor(OwnerTint tint, DistrictOwnershipService ownership)
        {
            string owner = null;
            if (tint.DistrictCenter)
            {
                owner = ownership.GetDistrictOwner(tint.DistrictCenter);
            }
            else if (tint.BlockObject && !tint.BlockObject.IsFinished)
            {
                // Construction sites show who placed them, since only
                // that player's districts build them (see PlacedBy)
                owner = tint.PlacedBy?.PlayerID;
            }
            else if (tint.IsPath && tint.BlockObject)
            {
                // A path belongs to the district whose roads it's part of
                Vector3 position = CoordinateSystem.GridToWorldCentered(tint.BlockObject.Coordinates);
                foreach (DistrictCenter district in _districtCenterRegistry.FinishedDistrictCenters)
                {
                    if (district.IsOnInstantDistrictRoad(position))
                    {
                        owner = ownership.GetDistrictOwner(district);
                        break;
                    }
                }
            }
            Color? color = ownership.GetPlayerColor(owner);
            if (!color.HasValue) return null;
            Color c = color.Value * (UsesGlyph(tint) ? GlyphStrength : Strength);
            c.a = 1f;
            return c;
        }

        private void Apply(OwnerTint tint, Color? color)
        {
            if (color.HasValue) _highlighter.HighlightSecondary(tint, color.Value);
            else if (tint.AppliedColor.HasValue) _highlighter.UnhighlightSecondary(tint);
            tint.AppliedColor = color;
        }

        private void ApplyGlyph(OwnerTint tint, Color? color)
        {
            // In case the district center was tinted whole before it finished
            if (tint.AppliedColor.HasValue)
            {
                _highlighter.UnhighlightSecondary(tint);
                tint.AppliedColor = null;
            }
            Color target = color ?? Color.clear;
            foreach (Material material in tint.GetGlyphMaterials(this))
            {
                if (material) material.SetColor(EmissionColorProperty, target);
            }
            tint.GlyphColor = color;
        }

        private void ClearAll()
        {
            foreach (OwnerTint tint in _tints)
            {
                if (!tint) continue;
                if (tint.AppliedColor.HasValue) _highlighter.UnhighlightSecondary(tint);
                tint.AppliedColor = null;
                if (tint.GlyphColor.HasValue) ApplyGlyph(tint, null);
                if (tint.DistrictCenter) ApplyFire(tint, null);
            }
        }
    }
}
