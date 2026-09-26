using BeaverBuddies.Events;
using BeaverBuddies.IO;
using BeaverBuddies.Players;
using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.BaseComponentSystem;
using Timberborn.Persistence;
using Timberborn.ScienceSystem;
using Timberborn.SingletonSystem;
using Timberborn.WorldPersistence;

namespace BeaverBuddies.Science
{
    /**
     * The player that science added or spent right now belongs to. Set
     * while replaying an event (the player who sent it) and while a
     * district's building produces or uses science (the district's owner).
     * A null owner means the shared pool.
     */
    public static class ScienceContext
    {
        public static bool Active { get; private set; }
        public static string Owner { get; private set; }

        public static Scope Use(string owner)
        {
            var scope = new Scope(Active, Owner);
            Active = true;
            Owner = owner;
            return scope;
        }

        public readonly struct Scope : IDisposable
        {
            private readonly bool _wasActive;
            private readonly string _previousOwner;

            public Scope(bool wasActive, string previousOwner)
            {
                _wasActive = wasActive;
                _previousOwner = previousOwner;
            }

            public void Dispose()
            {
                Active = _wasActive;
                Owner = _previousOwner;
            }
        }
    }

    /**
     * Splits the game's science points between players. The game's own
     * total stays untouched (so saves and vanilla code keep working), and
     * this service tracks how much of it belongs to each player. Whatever
     * isn't anyone's is the shared pool, which everyone can spend.
     *
     * Everything here changes only from the simulation and from replayed
     * events, so it's the same on every machine and travels in the save.
     */
    public class PlayerScienceService : RegisteredSingleton, ISaveableSingleton, ILoadableSingleton, IUpdatableSingleton
    {
        private static readonly SingletonKey PlayerScienceKey = new SingletonKey("BeaverBuddies.PlayerScience");
        private static readonly PropertyKey<bool> EnabledKey = new PropertyKey<bool>("Enabled");
        private static readonly ListKey<string> PointsKey = new ListKey<string>("Points");
        private const char Separator = '\t';

        private readonly ScienceService _scienceService;
        private readonly ISingletonLoader _singletonLoader;

        private readonly Dictionary<string, int> _points = new();

        private bool? _requestedEnabled;

        public bool Enabled { get; private set; } = true;

        public static PlayerScienceService Instance => SingletonManager.GetSingleton<PlayerScienceService>();

        public PlayerScienceService(ScienceService scienceService, ISingletonLoader singletonLoader)
        {
            _scienceService = scienceService;
            _singletonLoader = singletonLoader;
        }

        public void Load()
        {
            if (!_singletonLoader.TryGetSingleton(PlayerScienceKey, out IObjectLoader loader)) return;
            if (loader.Has(EnabledKey)) Enabled = loader.Get(EnabledKey);
            foreach (var (id, points) in SaveUtils.Split(loader, PointsKey, Separator))
            {
                if (int.TryParse(points, out int value)) _points[id] = value;
            }
        }

        public void Save(ISingletonSaver singletonSaver)
        {
            IObjectSaver saver = singletonSaver.GetSingleton(PlayerScienceKey);
            saver.Set(EnabledKey, Enabled);
            saver.Set(PointsKey, SaveUtils.Join(_points.ToDictionary(p => p.Key, p => p.Value.ToString()), Separator));
        }

        public void UpdateSingleton()
        {
            // The host decides whether science is per player. The event
            // plays on a later tick, so only ask once per change.
            if (!(EventIO.Get() is ServerEventIO) || ReplayEvent.GetReplayServiceIfReady() == null) return;
            bool wanted = Settings.SciencePerPlayer;
            if (wanted == Enabled || wanted == _requestedEnabled) return;
            _requestedEnabled = wanted;
            if (ReplayEvent.DoPrefix(() => new SciencePerPlayerSetEvent() { enabled = wanted }))
            {
                SetEnabled(wanted);
            }
        }

        public void SetEnabled(bool enabled)
        {
            Enabled = enabled;
        }

        public void OnPointsAdded(int amount)
        {
            if (!Enabled || !ScienceContext.Active || ScienceContext.Owner == null) return;
            _points.TryGetValue(ScienceContext.Owner, out int current);
            _points[ScienceContext.Owner] = current + amount;
        }

        public void OnPointsSubtracted(int amount)
        {
            // The owner pays first, whatever is left comes out of the shared pool
            if (Enabled && ScienceContext.Active && ScienceContext.Owner != null
                && _points.TryGetValue(ScienceContext.Owner, out int current))
            {
                _points[ScienceContext.Owner] = current - Math.Min(current, amount);
            }
            Normalize();
        }

        /**
         * If the total dropped below what players own (e.g. something
         * outside this service spent points), take the difference from
         * the players in a fixed order so every machine agrees.
         */
        private void Normalize()
        {
            int excess = _points.Values.Sum() - _scienceService.SciencePoints;
            if (excess <= 0) return;
            foreach (string id in _points.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList())
            {
                int take = Math.Min(_points[id], excess);
                _points[id] -= take;
                excess -= take;
                if (excess <= 0) break;
            }
        }

        public int SharedPoints => Math.Max(0, _scienceService.SciencePoints - _points.Values.Sum());

        /**
         * What a player can spend: their own points plus the shared pool.
         * A null player can only spend the shared pool.
         */
        public int AvailablePoints(string playerID)
        {
            if (!Enabled) return _scienceService.SciencePoints;
            int own = 0;
            if (playerID != null) _points.TryGetValue(playerID, out own);
            return Math.Min(_scienceService.SciencePoints, own + SharedPoints);
        }

        /**
         * Whose science a check or a payment should use right now: the
         * current context if there is one, otherwise this player (UI).
         */
        public static string CurrentPayer => ScienceContext.Active ? ScienceContext.Owner : PlayerIdentity.LocalID;

        /**
         * The player whose science a building earns or uses: its
         * district's owner, or null for the shared pool.
         */
        public static string GetOwner(BaseComponent building)
        {
            return DistrictOwnershipService.Instance?.GetOwner(building);
        }
    }

    [Serializable]
    public class SciencePerPlayerSetEvent : ReplayEvent
    {
        public bool enabled;

        public override void Replay(IReplayContext context)
        {
            PlayerScienceService.Instance?.SetEnabled(enabled);
        }

        public override string ToActionString()
        {
            return $"Science per player: {enabled}";
        }
    }
}
