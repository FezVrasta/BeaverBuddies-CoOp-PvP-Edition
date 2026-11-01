using System;
using System.Collections.Generic;
using System.Linq;
using BeaverBuddies.Events;
using BeaverBuddies.IO;
using BeaverBuddies.Modding;
using BeaverBuddies.Players;
using Timberborn.Persistence;
using Timberborn.SingletonSystem;
using Timberborn.WorldPersistence;

namespace BeaverBuddies.Factions
{
    /**
     * The faction each player picked, kept in the game itself: hosting
     * saves and reloads the game, and the players come back to it later,
     * so a pick that lived only on one machine would be lost. Each player
     * announces theirs once their game is loaded, and again when they pick
     * another in the lobby; every machine notes it.
     */
    public class PlayerFactionService : RegisteredSingleton, ISaveableSingleton, ILoadableSingleton, IUpdatableSingleton
    {
        // Timber Empires' key, from before the picks moved here: its saves keep theirs
        private static readonly SingletonKey Key = new("TimberEmpires.PlayerFactions");
        private static readonly ListKey<string> FactionsKey = new("Factions");
        private const char Separator = '\t';

        // A pick for the game on this machine, until it's announced
        internal static string Pending;

        // The same pick, kept for the whole match: the toolbar can be put together after the pick is
        // announced and before the announcement has come back, when neither Pending nor the save has it
        internal static string Picked;

        private readonly ISingletonLoader _singletonLoader;
        private readonly Dictionary<string, string> _factions = new();
        private bool _loaded;

        public static PlayerFactionService Instance => SingletonManager.GetSingleton<PlayerFactionService>();

        public PlayerFactionService(ISingletonLoader singletonLoader)
        {
            _singletonLoader = singletonLoader;
            // Kept while a match's host saves and hosts its game; a game joined starts from the save's
            if (Pending == null && EventIO.Get() is not ServerEventIO) Picked = null;
        }

        public void Load() => LoadIfNeeded();

        /**
         * The picks the save has, read the first time anything asks: the
         * toolbar can be put together before this loads, and a player
         * joining a saved game has no match pick to go by.
         */
        private void LoadIfNeeded()
        {
            if (_loaded) return;
            _loaded = true;
            if (!_singletonLoader.TryGetSingleton(Key, out IObjectLoader loader) || !loader.Has(FactionsKey)) return;
            foreach (string entry in loader.Get(FactionsKey))
            {
                int index = entry.IndexOf(Separator);
                if (index > 0) _factions[entry.Substring(0, index)] = entry.Substring(index + 1);
            }
            Plugin.Log($"[Factions] The save has {_factions.Count} players' factions");
        }

        public void Save(ISingletonSaver singletonSaver) =>
            singletonSaver.GetSingleton(Key).Set(FactionsKey, _factions.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + Separator + p.Value).ToList());

        public string Of(string player)
        {
            LoadIfNeeded();
            return player != null && _factions.TryGetValue(player, out string faction) ? faction : null;
        }

        public void Set(string player, string faction)
        {
            if (string.IsNullOrEmpty(player) || string.IsNullOrEmpty(faction)) return;
            LoadIfNeeded();
            _factions[player] = faction;
            Plugin.Log($"[Factions] {player} plays {faction}");
            if (player != PlayerIdentity.LocalID) return;
            // Heard back: the game has it now, and a later game won't take it for its own
            if (Picked == faction) Picked = null;
            FactionToolbar.Refresh();
        }

        // Alone, straight away; in multiplayer, once events can go out
        public void UpdateSingleton()
        {
            if (Pending == null || PlayerIdentity.LocalID == null) return;
            if (!EventIO.IsNull && !ModBridge.CanRecord()) return;
            string faction = Pending;
            Pending = null;
            if (ReplayEvent.DoPrefix(() => new PlayerFactionEvent() { faction = faction })) Set(PlayerIdentity.LocalID, faction);
        }

        /** Announces the faction of whoever is acting now (an AI opponent, under ActAs). */
        public static void Announce(string faction)
        {
            if (ReplayEvent.DoPrefix(() => new PlayerFactionEvent() { faction = faction })) Instance?.Set(ModBridge.ActingPlayerID(), faction);
        }
    }

    [Serializable]
    public class PlayerFactionEvent : ReplayEvent
    {
        public string faction;

        public override void Replay(IReplayContext context) => PlayerFactionService.Instance?.Set(playerID, faction);

        public override string ToActionString() => $"Player {playerID} plays {faction}";
    }

    public static class LocalFaction
    {
        // A match is starting: its faction for this player
        public static void StartMatch(string faction)
        {
            PlayerFactionService.Pending = PlayerFactionService.Picked = faction;
            Plugin.Log($"[Factions] Match starting as {faction}");
        }

        /**
         * Picked in the lobby, before the host starts: saved as this
         * player's faction, announced, and their toolbar switched to it.
         */
        public static void Pick(string faction)
        {
            if (FactionRules.GetFaction(faction) == null) return;
            Settings.SetMyFaction(faction);
            PlayerFactionService.Pending = PlayerFactionService.Picked = faction;
            FactionToolbar.Refresh();
        }

        /**
         * The faction this player builds with: the one they picked for this
         * game, else (a guest only: a host's game is the faction they
         * picked for it) their "My faction" setting, or the host's faction
         * if it's automatic or not in this game.
         */
        public static string Id
        {
            get
            {
                // The pick as this game loads, before it's announced and noted: the toolbar is put together then
                // and one picked in the lobby, before the host has heard it
                string picked = PlayerFactionService.Picked ?? PlayerFactionService.Instance?.Of(PlayerIdentity.LocalID);
                if (picked != null && FactionRules.GetFaction(picked) != null) return picked;
                string chosen = !EventIO.IsNull && !ModBridge.IsHost() ? Settings.MyFaction : null;
                if (!string.IsNullOrEmpty(chosen) && FactionRules.GetFaction(chosen) != null) return chosen;
                return FactionRules.HostFaction;
            }
        }

        // Whether this player builds it, or a list (tooltips, panels) shows it to them
        public static bool CanBuild(Timberborn.BlueprintSystem.ComponentSpec spec) => MixedFactionsService.FactionHas(Specs.Name(spec), Id);
    }
}
