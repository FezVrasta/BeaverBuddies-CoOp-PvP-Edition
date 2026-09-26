using BeaverBuddies.Events;
using BeaverBuddies.IO;
using BeaverBuddies.Util;
using System;
using System.Collections.Generic;
using System.Reflection;
using Timberborn.EntitySystem;
using Timberborn.GameDistricts;
using Timberborn.Persistence;
using Timberborn.QuickNotificationSystem;
using Timberborn.SingletonSystem;
using Timberborn.WorldPersistence;

namespace BeaverBuddies.Players
{
    /**
     * Marks events that already check who may do them (trade deals, power
     * limits, ownership changes), so border protection leaves them alone.
     */
    public interface IOwnershipChecked
    {
    }

    /**
     * Stops players from demolishing, pausing or changing the settings of
     * buildings (and beavers) that belong to someone else's district.
     * Checked when an action is recorded, to tell the player, and again when
     * it replays on every machine, so it can't be bypassed.
     */
    public class BorderProtection : RegisteredSingleton, ISaveableSingleton, ILoadableSingleton, IUpdatableSingleton
    {
        private static readonly SingletonKey BorderProtectionKey = new SingletonKey("BeaverBuddies.BorderProtection");
        private static readonly PropertyKey<bool> EnabledKey = new PropertyKey<bool>("Enabled");

        private static readonly Dictionary<Type, (FieldInfo single, FieldInfo list)> EntityFields = new();

        private readonly ISingletonLoader _singletonLoader;
        private readonly EntityRegistry _entityRegistry;
        private readonly QuickNotificationService _quickNotificationService;
        private bool? _requestedEnabled;

        public bool Enabled { get; private set; } = true;

        public static BorderProtection Instance => SingletonManager.GetSingleton<BorderProtection>();

        public BorderProtection(ISingletonLoader singletonLoader, EntityRegistry entityRegistry,
            QuickNotificationService quickNotificationService)
        {
            _singletonLoader = singletonLoader;
            _entityRegistry = entityRegistry;
            _quickNotificationService = quickNotificationService;
        }

        public void Load()
        {
            if (_singletonLoader.TryGetSingleton(BorderProtectionKey, out IObjectLoader loader) && loader.Has(EnabledKey))
            {
                Enabled = loader.Get(EnabledKey);
            }
        }

        public void Save(ISingletonSaver singletonSaver)
        {
            singletonSaver.GetSingleton(BorderProtectionKey).Set(EnabledKey, Enabled);
        }

        public void UpdateSingleton()
        {
            // Only the host's setting counts; the event plays on a later tick
            if (!(EventIO.Get() is ServerEventIO) || ReplayEvent.GetReplayServiceIfReady() == null) return;
            bool wanted = Settings.BorderProtection;
            if (wanted == Enabled || wanted == _requestedEnabled) return;
            _requestedEnabled = wanted;
            if (ReplayEvent.DoPrefix(() => new BorderProtectionSetEvent() { enabled = wanted })) Enabled = wanted;
        }

        public void SetEnabled(bool enabled) => Enabled = enabled;

        /**
         * The player a building or beaver belongs to: the owner of its
         * district, or whoever placed it if it isn't in a district yet.
         * Null if it belongs to nobody.
         */
        public static string OwnerOf(EntityComponent entity)
        {
            var ownership = DistrictOwnershipService.Instance;
            if (ownership == null || !entity) return null;

            DistrictCenter district = entity.GetComponent<DistrictCenter>();
            if (!district) district = entity.GetComponent<DistrictBuilding>()?.GetDistrictOrConstructionDistrict();
            if (!district) district = entity.GetComponent<Citizen>()?.AssignedDistrict;
            string owner = district ? ownership.GetDistrictOwner(district) : null;
            return owner ?? entity.GetComponent<PlacedBy>()?.PlayerID;
        }

        /**
         * Removes what the player may not touch from the event. Returns
         * false if nothing is left, so the event should be dropped.
         */
        public bool Filter(ReplayEvent replayEvent, string playerID, out string blockedOwner)
        {
            blockedOwner = null;
            if (!Enabled || replayEvent is IOwnershipChecked || playerID == null) return true;

            var (single, list) = GetEntityFields(replayEvent.GetType());
            if (single != null && single.GetValue(replayEvent) is string entityID)
            {
                string owner = Owner(entityID);
                if (owner != null && owner != playerID)
                {
                    blockedOwner = owner;
                    return false;
                }
            }
            if (list != null && list.GetValue(replayEvent) is List<string> entityIDs)
            {
                int before = entityIDs.Count;
                for (int i = entityIDs.Count - 1; i >= 0; i--)
                {
                    string owner = Owner(entityIDs[i]);
                    if (owner == null || owner == playerID) continue;
                    blockedOwner = owner;
                    entityIDs.RemoveAt(i);
                }
                if (before > 0 && entityIDs.Count == 0) return false;
            }
            return true;
        }

        /**
         * Called when this player does something: drops it (with a notice)
         * if it touches someone else's buildings.
         */
        public bool AllowLocal(ReplayEvent replayEvent)
        {
            if (Filter(replayEvent, replayEvent.playerID, out string owner)) return true;
            string name = DistrictOwnershipService.Instance?.GetPlayerName(owner);
            _quickNotificationService.SendWarningNotification(
                string.Format(RegisteredLocalizationService.T("BeaverBuddies.BorderProtection.Blocked"), name));
            return false;
        }

        private string Owner(string entityID)
        {
            if (!Guid.TryParse(entityID, out Guid guid)) return null;
            return OwnerOf(_entityRegistry.GetEntity(guid));
        }

        private static (FieldInfo, FieldInfo) GetEntityFields(Type type)
        {
            if (!EntityFields.TryGetValue(type, out var fields))
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                FieldInfo single = type.GetField("entityID", flags);
                FieldInfo list = type.GetField("entityIDs", flags);
                fields = (single?.FieldType == typeof(string) ? single : null,
                    list?.FieldType == typeof(List<string>) ? list : null);
                EntityFields[type] = fields;
            }
            return fields;
        }
    }

    [Serializable]
    public class BorderProtectionSetEvent : ReplayEvent
    {
        public bool enabled;

        public override void Replay(IReplayContext context)
        {
            BorderProtection.Instance?.SetEnabled(enabled);
        }

        public override string ToActionString()
        {
            return $"Border protection: {enabled}";
        }
    }
}
