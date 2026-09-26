using BeaverBuddies.Events;
using BeaverBuddies.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.GameDistricts;
using Timberborn.Persistence;
using Timberborn.SingletonSystem;
using Timberborn.WorldPersistence;
using UnityEngine;

namespace BeaverBuddies.Players
{
    /**
     * Which players have been in this game and which districts belong to
     * whom. Changes only through replayed events, so it's the same on
     * every machine and travels in the save.
     */
    public class DistrictOwnershipService : RegisteredSingleton, ISaveableSingleton, ILoadableSingleton, IUpdatableSingleton
    {
        private static readonly SingletonKey DistrictOwnershipKey = new SingletonKey("BeaverBuddies.DistrictOwnership");
        private static readonly ListKey<string> PlayersKey = new ListKey<string>("Players");
        private static readonly ListKey<string> DistrictOwnersKey = new ListKey<string>("DistrictOwners");
        // Saves from the first per-player science build kept these there
        private static readonly SingletonKey LegacyKey = new SingletonKey("BeaverBuddies.PlayerScience");
        private const char Separator = '\t';

        private readonly ISingletonLoader _singletonLoader;
        private readonly IBlockService _blockService;

        private readonly Dictionary<string, string> _playerNames = new();
        private readonly Dictionary<string, string> _districtOwners = new();

        private bool _announced;
        private string _announcedName;
        private float _nextCheckTime;

        public static DistrictOwnershipService Instance => SingletonManager.GetSingleton<DistrictOwnershipService>();

        public IReadOnlyDictionary<string, string> PlayerNames => _playerNames;

        public DistrictOwnershipService(ISingletonLoader singletonLoader, IBlockService blockService)
        {
            _singletonLoader = singletonLoader;
            _blockService = blockService;
        }

        public void Load()
        {
            if (!_singletonLoader.TryGetSingleton(DistrictOwnershipKey, out IObjectLoader loader)
                && !_singletonLoader.TryGetSingleton(LegacyKey, out loader))
            {
                return;
            }
            foreach (var (id, name) in SaveUtils.Split(loader, PlayersKey, Separator))
            {
                _playerNames[id] = name;
            }
            foreach (var (district, owner) in SaveUtils.Split(loader, DistrictOwnersKey, Separator))
            {
                _districtOwners[district] = owner;
            }
        }

        public void Save(ISingletonSaver singletonSaver)
        {
            IObjectSaver saver = singletonSaver.GetSingleton(DistrictOwnershipKey);
            saver.Set(PlayersKey, SaveUtils.Join(_playerNames, Separator));
            saver.Set(DistrictOwnersKey, SaveUtils.Join(_districtOwners, Separator));
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
            if (_announced && name == _announcedName) return;
            _announced = true;
            _announcedName = name;
            if (ReplayEvent.DoPrefix(() => new PlayerAnnouncedEvent() { name = name }))
            {
                SetPlayerName(PlayerIdentity.LocalID, name);
            }
        }

        public void SetPlayerName(string playerID, string name)
        {
            if (string.IsNullOrEmpty(playerID)) return;
            _playerNames[playerID] = name ?? string.Empty;
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
    }

    public static class SaveUtils
    {
        public static IEnumerable<(string, string)> Split(IObjectLoader loader, ListKey<string> key, char separator)
        {
            if (!loader.Has(key)) yield break;
            foreach (string entry in loader.Get(key))
            {
                int index = entry.IndexOf(separator);
                if (index <= 0) continue;
                yield return (entry.Substring(0, index), entry.Substring(index + 1));
            }
        }

        public static List<string> Join(IReadOnlyDictionary<string, string> values, char separator)
        {
            return values
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Key + separator + p.Value)
                .ToList();
        }
    }

    [Serializable]
    public class PlayerAnnouncedEvent : ReplayEvent
    {
        public string name;

        public override void Replay(IReplayContext context)
        {
            DistrictOwnershipService.Instance?.SetPlayerName(playerID, name);
        }

        public override string ToActionString()
        {
            return $"Player {playerID} is {name}";
        }
    }

    [Serializable]
    public class DistrictOwnerSetEvent : ReplayEvent
    {
        public string districtID;
        public string ownerID;

        public override void Replay(IReplayContext context)
        {
            DistrictOwnershipService.Instance?.SetDistrictOwner(districtID, ownerID);
        }

        public override string ToActionString()
        {
            return $"District {districtID} now belongs to {ownerID ?? "nobody"}";
        }
    }
}
