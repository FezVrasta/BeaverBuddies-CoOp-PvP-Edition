using BeaverBuddies.IO;
using System;
using Timberborn.BaseComponentSystem;
using Timberborn.Buildings;
using Timberborn.EntitySystem;
using Timberborn.TemplateSystem;
using static BeaverBuddies.SingletonManager;

namespace BeaverBuddies.Events
{
    public interface IReplayContext
    {
        T GetSingleton<T>();
    }

    public abstract class ReplayEvent : IComparable<ReplayEvent>
    {
        public static readonly string LocalPlayerID = GuidPatcher.RealNewGuid().ToString();

        public int ticksSinceLoad;
        public int? randomS0Before;
        // The stable ID of the player who caused this event. Stamped when
        // the event is recorded, not when it's made: a field initializer
        // would also run as an event arrives, and fill in each receiving
        // machine's own ID wherever the sender left it out
        public string playerID;
        // For measuring how long actions take (Performance/ActionLatency):
        // when it was made, on its player's machine, and how long the host
        // held it for the next tick. Neither changes what it does
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public long? sentAtMs;
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public int? hostWaitMs;

        public string type => GetType().Name;

        public int CompareTo(ReplayEvent other)
        {
            if (other == null)
                return 1;
            //return timeInFixedSecs.CompareTo(other.timeInFixedSecs);
            return ticksSinceLoad.CompareTo(other.ticksSinceLoad);
        }

        public abstract void Replay(IReplayContext context);

        public override string ToString()
        {
            return type;
        }

        public virtual string ToActionString()
        {
            return $"Doing: {type}";
        }

        public static EntityComponent GetEntityComponent(IReplayContext context, string entityID)
        {
            if (!Guid.TryParse(entityID, out Guid guid))
            {
                Plugin.LogWarning($"Could not parse guid: {entityID}");
                return null;
            }
            var entity = context.GetSingleton<EntityRegistry>().GetEntity(guid);
            if (entity == null)
            {
                Plugin.LogWarning($"Could not find entity: {entityID}");
            }
            return entity;
        }

        public static T GetComponent<T>(IReplayContext context, string entityID)
        {
            var entity = GetEntityComponent(context, entityID);
            if (entity == null) return default;
            var component = entity.GetComponent<T>();
            if (component == null)
            {
                Plugin.LogWarning($"Could not find component {typeof(T)} on entity {entityID}");
            }
            return component;
        }

        public static string GetEntityID(BaseComponent component)
        {
            if (component == null) return null;
            try
            {
                return component.GetComponent<EntityComponent>()?.EntityId.ToString();
            }
            catch (Exception)
            {
                // The component may not be fully initialized/registered as an
                // entity yet (e.g. a duplication source while a tool is mid-use),
                // in which case GetComponent throws internally in ComponentCache
                // instead of returning null. Treat that as "no entity" so the
                // caller falls back to the base behaviour instead of crashing.
                return null;
            }
        }

        protected BuildingSpec GetBuilding(IReplayContext context, string buildingName)
        {
            var result = context.GetSingleton<BuildingService>().GetBuildingTemplate(buildingName);
            if (result == null)
            {
                Plugin.LogWarning($"Could not find building prefab: {buildingName}");
            }
            return result;
        }

        public static string GetBuildingName(EntitySetup.Builder entitySetupBuilder)
        {
            var spec = entitySetupBuilder.Template.GetSpec<BuildingSpec>();
            return spec?.GetSpec<TemplateSpec>()?.TemplateName;
        }

        private static int _unrecorded;

        private class UnrecordedScope : IDisposable
        {
            public void Dispose() => _unrecorded--;
        }

        /**
         * Lets what's done inside run as is, on every machine alike, rather
         * than go out as a player's event: for game logic acting on ticks
         * through methods players also call from the UI.
         */
        public static IDisposable Unrecorded()
        {
            _unrecorded++;
            return new UnrecordedScope();
        }

        public static ReplayService GetReplayServiceIfReady()
        {
            // If we haven't loaded yet, we're not ready
            if (!ReplayService.IsLoaded) return null;

            var replayService = GetSingleton<ReplayService>();
            if (replayService == null || replayService.IsDesynced) return null;
            return replayService;
        }
        

        /// <summary>
        /// Helper method to make overriding recorded actions in game easier.
        /// </summary>
        /// <param name="getEvent">
        /// A function that returns the event to record, or null
        /// if we should skip recording and do the default method behavior.
        /// </param>
        /// <returns>True if the method should use default behavior</returns>
        public static bool DoPrefix(Func<ReplayEvent> getEvent)
        {
            // If we're already replaying events, just let the original method run.
            // This handles nested calls (e.g., Replay() calls Unlock() which triggers this prefix again)
            if (ReplayService.IsReplayingEvents || _unrecorded > 0) return true;

            // If the replay service is not available, just use default behavior
            ReplayService replayService = GetReplayServiceIfReady();
            if (replayService == null) return true;

            // Get the event and if it's null, just use default behavior
            ReplayEvent message = getEvent();
            if (message == null) return true;

            // Optional: Log the message
            Plugin.Log(message.ToActionString());

            // Record the event
            replayService.RecordEvent(message);

            // Return based on the EventIO's desired behavior
            return EventIO.ShouldPlayPatchedEvents;
        }

        public static bool DoEntityPrefix(BaseComponent component, Func<string, ReplayEvent> doRecord)
        {
            return DoPrefix(() =>
            {
                string entityID = GetEntityID(component);
                // If this is happening to a non-entity (e.g. prefab),
                // just let the base method handle it
                if (entityID == null) return null;
                return doRecord(entityID);
            });
        }
    }

}
