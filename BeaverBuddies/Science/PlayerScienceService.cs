using BeaverBuddies.Events;
using BeaverBuddies.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.GameDistricts;
using Timberborn.Persistence;
using Timberborn.ScienceSystem;
using Timberborn.SingletonSystem;
using Timberborn.WorldPersistence;
using UnityEngine;

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
        private static readonly ListKey<string> PlayersKey = new ListKey<string>("Players");
        private static readonly ListKey<string> PointsKey = new ListKey<string>("Points");
        private static readonly ListKey<string> DistrictOwnersKey = new ListKey<string>("DistrictOwners");
        private const char Separator = '\t';

        private readonly ScienceService _scienceService;
        private readonly ISingletonLoader _singletonLoader;
        private readonly IBlockService _blockService;

        private readonly Dictionary<string, string> _playerNames = new();
        private readonly Dictionary<string, int> _points = new();
        private readonly Dictionary<string, string> _districtOwners = new();

        private bool _announced;
        private string _announcedName;
        private bool? _requestedEnabled;
        private float _nextCheckTime;

        public bool Enabled { get; private set; } = true;

        public static PlayerScienceService Instance => SingletonManager.GetSingleton<PlayerScienceService>();

        public IReadOnlyDictionary<string, string> PlayerNames => _playerNames;

        public PlayerScienceService(ScienceService scienceService, ISingletonLoader singletonLoader,
            IBlockService blockService)
        {
            _scienceService = scienceService;
            _singletonLoader = singletonLoader;
            _blockService = blockService;
        }

        public void Load()
        {
            if (!_singletonLoader.TryGetSingleton(PlayerScienceKey, out IObjectLoader loader)) return;
            if (loader.Has(EnabledKey)) Enabled = loader.Get(EnabledKey);
            foreach (var (id, name) in Split(loader, PlayersKey))
            {
                _playerNames[id] = name;
            }
            foreach (var (id, points) in Split(loader, PointsKey))
            {
                if (int.TryParse(points, out int value)) _points[id] = value;
            }
            foreach (var (district, owner) in Split(loader, DistrictOwnersKey))
            {
                _districtOwners[district] = owner;
            }
        }

        public void Save(ISingletonSaver singletonSaver)
        {
            IObjectSaver saver = singletonSaver.GetSingleton(PlayerScienceKey);
            saver.Set(EnabledKey, Enabled);
            saver.Set(PlayersKey, Join(_playerNames));
            saver.Set(PointsKey, Join(_points.ToDictionary(p => p.Key, p => p.Value.ToString())));
            saver.Set(DistrictOwnersKey, Join(_districtOwners));
        }

        public void UpdateSingleton()
        {
            // Tell the others who we are once the co-op game is running,
            // and again if the name changes
            if (EventIO.IsNull || ReplayEvent.GetReplayServiceIfReady() == null) return;
            // The name can come from Steam, so don't ask every frame
            if (Time.unscaledTime < _nextCheckTime) return;
            _nextCheckTime = Time.unscaledTime + 1f;
            string name = PlayerIdentity.LocalName;
            if (!_announced || name != _announcedName)
            {
                _announced = true;
                _announcedName = name;
                RecordOrApply(new PlayerAnnouncedEvent() { name = name }, () => SetPlayerName(PlayerIdentity.LocalID, name));
            }

            // The host decides whether science is per player. The event
            // plays on a later tick, so only ask once per change.
            bool wanted = Settings.SciencePerPlayer;
            if (EventIO.Get() is ServerEventIO && wanted != Enabled && wanted != _requestedEnabled)
            {
                _requestedEnabled = wanted;
                RecordOrApply(new SciencePerPlayerSetEvent() { enabled = wanted }, () => SetEnabled(wanted));
            }
        }

        // ---- State changes (from the simulation or replayed events) ----

        public void SetPlayerName(string playerID, string name)
        {
            if (string.IsNullOrEmpty(playerID)) return;
            _playerNames[playerID] = name ?? string.Empty;
        }

        public void SetEnabled(bool enabled)
        {
            Enabled = enabled;
        }

        public void SetDistrictOwner(string districtID, string playerID)
        {
            if (string.IsNullOrEmpty(districtID)) return;
            if (string.IsNullOrEmpty(playerID))
            {
                _districtOwners.Remove(districtID);
            }
            else
            {
                _districtOwners[districtID] = playerID;
            }
        }

        /**
         * A new district center belongs to the player who placed it.
         */
        public void OnBuildingPlaced(Vector3Int coordinates, string playerID)
        {
            if (string.IsNullOrEmpty(playerID)) return;
            DistrictCenter districtCenter = _blockService.GetObjectsWithComponentAt<DistrictCenter>(coordinates).FirstOrDefault();
            if (districtCenter == null) return;
            SetDistrictOwner(ReplayEvent.GetEntityID(districtCenter), playerID);
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

        // ---- Queries ----

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

        public string GetDistrictOwner(DistrictCenter districtCenter)
        {
            string id = ReplayEvent.GetEntityID(districtCenter);
            if (id == null) return null;
            return _districtOwners.TryGetValue(id, out string owner) ? owner : null;
        }

        /**
         * The owner of the district a building belongs to, or null.
         */
        public string GetOwner(BaseComponent building)
        {
            DistrictCenter district = building.GetComponent<DistrictBuilding>()?.District;
            return district == null ? null : GetDistrictOwner(district);
        }

        public string GetPlayerName(string playerID)
        {
            if (playerID == null) return null;
            return _playerNames.TryGetValue(playerID, out string name) && !string.IsNullOrEmpty(name)
                ? name
                : Settings.DefaultPingPlayerName;
        }

        private static void RecordOrApply(ReplayEvent replayEvent, Action apply)
        {
            if (ReplayEvent.DoPrefix(() => replayEvent)) apply();
        }

        private static IEnumerable<(string, string)> Split(IObjectLoader loader, ListKey<string> key)
        {
            if (!loader.Has(key)) yield break;
            foreach (string entry in loader.Get(key))
            {
                int index = entry.IndexOf(Separator);
                if (index <= 0) continue;
                yield return (entry.Substring(0, index), entry.Substring(index + 1));
            }
        }

        private static List<string> Join(IReadOnlyDictionary<string, string> values)
        {
            return values
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Key + Separator + p.Value)
                .ToList();
        }
    }

    [Serializable]
    public class PlayerAnnouncedEvent : ReplayEvent
    {
        public string name;

        public override void Replay(IReplayContext context)
        {
            PlayerScienceService.Instance?.SetPlayerName(playerID, name);
        }

        public override string ToActionString()
        {
            return $"Player {playerID} is {name}";
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

    [Serializable]
    public class DistrictOwnerSetEvent : ReplayEvent
    {
        public string districtID;
        public string ownerID;

        public override void Replay(IReplayContext context)
        {
            PlayerScienceService.Instance?.SetDistrictOwner(districtID, ownerID);
        }

        public override string ToActionString()
        {
            return $"District {districtID} now belongs to {ownerID ?? "everyone"}";
        }
    }
}
