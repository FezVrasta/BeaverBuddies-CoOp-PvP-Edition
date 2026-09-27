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

        private List<MeshRenderer> _glyphRenderers;

        /**
         * The renderers of the faction glyph overlay, attached to the finished
         * district center the first time it's asked for. The game draws the
         * district center with one merged material, so the glyph gets its own
         * copy on top to be tinted alone.
         */
        public List<MeshRenderer> GetGlyphRenderers(OwnerTintService service)
        {
            if (_glyphRenderers != null) return _glyphRenderers;
            if (!DistrictCenter || !BlockObject || !BlockObject.IsFinished) return Empty;
            BuildingModel model = GetComponent<BuildingModel>();
            if (!model || !model.FinishedModel) return Empty;
            _glyphRenderers = service.AttachGlyph(model.FinishedModel.transform, GameObject.name);
            return _glyphRenderers;
        }

        private static readonly List<MeshRenderer> Empty = new();

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
     * Shows each player's color behind their district centers' faction
     * symbols, and as a faint tint on their paths and unbuilt construction sites, so
     * it's clear who owns what and who is going to build what. It only
     * changes how things look on this machine, never the game.
     */
    public class OwnerTintService : RegisteredSingleton, IUpdatableSingleton
    {
        // Highlights are added on top of the model, so a small fraction of
        // the color is enough (the game's own highlights are around 0.2)
        private const float Strength = 0.2f;
        // The glyph's material on both factions' district centers, used by
        // nothing else on them
        private const string GlyphPath = "Buildings/DistrictCenterGlyph/DistrictCenterGlyph";
        private static readonly int LightingMapProperty = Shader.PropertyToID("_LightingMap");
        private static readonly int LightingColorProperty = Shader.PropertyToID("_LightingColor");
        // In the shader's lighting scale, where the game's lights top out at 4
        private const float GlyphLighting = 0.6f;
        private const string GlyphObjectName = "#OwnerGlyph";
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

        public List<MeshRenderer> AttachGlyph(Transform finishedModel, string templateName)
        {
            var renderers = new List<MeshRenderer>();
            string faction = templateName.Contains("IronTeeth") ? "IronTeeth" : "Folktails";
            BinaryData data = _assetLoader.LoadSafe<BinaryData>($"{GlyphPath}.{faction}.Model");
            Texture2D mask = _assetLoader.LoadSafe<Texture2D>($"{GlyphPath}Mask.{faction}");
            if (!data || !mask)
            {
                Plugin.LogWarning($"No district center glyph model or mask for {faction}");
                return renderers;
            }
            var glyph = new GameObject(GlyphObjectName);
            glyph.transform.SetParent(finishedModel, false);
            using (var stream = new MemoryStream(data.Bytes))
            {
                _timbermeshImporter.Import(stream, glyph.transform);
            }
            renderers.AddRange(glyph.GetComponentsInChildren<MeshRenderer>(true));
            foreach (MeshRenderer renderer in renderers)
            {
                // Light the glyph through the mask, so only its background
                // takes the color and the symbol stays as it is
                foreach (Material material in renderer.materials)
                {
                    material.SetTexture(LightingMapProperty, mask);
                }
                renderer.SetShaderUserValue(0);
            }
            return renderers;
        }

        public void Register(OwnerTint tint) => _tints.Add(tint);

        public void Unregister(OwnerTint tint)
        {
            if (_tints.Remove(tint) && tint.AppliedColor.HasValue) _highlighter.UnhighlightSecondary(tint);
        }

        private bool UsesGlyph(OwnerTint tint) => tint.GetGlyphRenderers(this).Count > 0;

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
                    continue;
                }
                if (color == tint.AppliedColor && !reapply) continue;
                Apply(tint, color);
            }
            foreach (OwnerTint tint in _dead) _tints.Remove(tint);
            _dead.Clear();
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
            Color c = UsesGlyph(tint) ? color.Value : color.Value * Strength;
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
            // Same encoding as the game's MaterialLightingEnabler
            uint strength = color.HasValue ? (uint)(GlyphLighting / 4f * 255f) : 0u;
            foreach (MeshRenderer renderer in tint.GetGlyphRenderers(this))
            {
                if (!renderer) continue;
                if (color.HasValue)
                {
                    foreach (Material material in renderer.materials) material.SetColor(LightingColorProperty, color.Value);
                }
                renderer.SetShaderUserValue(strength);
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
            }
        }
    }
}
