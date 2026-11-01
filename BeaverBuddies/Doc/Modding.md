# Building mods on BeaverBuddies

BeaverBuddies keeps every machine in a game in sync by running the same simulation everywhere: each player's actions go out as events, and every machine plays them on the same tick. A mod that changes the game (new buildings, new orders, new rules) has to play by the same rules or the game falls out of sync.

## The bridge

A mod doesn't reference BeaverBuddies to compile. `BeaverBuddies.Modding.ModBridge` is a static class whose methods only take and return .NET, Unity and Timberborn types. Your mod finds it by name at runtime and binds each method to a delegate once, so calling it costs no more than a normal call. Hooks go in by name, through one method: `AddHook(name, delegate)`.

If BeaverBuddies isn't installed (or it's too old to have the bridge), the lookup finds nothing and your mod should treat the game as single player. Everything keeps working, it just doesn't sync.

`Version()` goes up whenever a method or a hook changes shape. Version 2 replaced the `Add...` methods with `AddHook`, merged the faction lookups into `FactionOf`, moved a faction's extra collections to `FactionCollectionsSpec` blueprints, and dropped the game mode and game setting methods, which are the Game Modes mod's now.

### Binding it

Put a small class in your mod that finds the bridge once and binds each method the first time it's used:

```csharp
public static class BeaverBuddiesBridge
{
    private static readonly Type BridgeType = AppDomain.CurrentDomain.GetAssemblies()
        .Select(a => a.GetType("BeaverBuddies.Modding.ModBridge", false))
        .FirstOrDefault(t => t != null);
    private static readonly Dictionary<string, Delegate> Bound = new();

    public static bool Available => BridgeType != null;

    private static T Bind<T>(string name) where T : Delegate
    {
        if (Bound.TryGetValue(name, out Delegate bound)) return (T)bound;
        MethodInfo method = BridgeType?.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
        T result = method == null ? null : (T)Delegate.CreateDelegate(typeof(T), method, false);
        Bound[name] = result;
        return result;
    }

    public static bool IsMultiplayer => Bind<Func<bool>>("IsMultiplayer")?.Invoke() ?? false;
    public static string LocalPlayerID => Bind<Func<string>>("LocalPlayerID")?.Invoke();

    public static void RegisterEvents(string mod, Action<object, string, Func<Type, object>> play) =>
        Bind<Action<string, Action<object, string, Func<Type, object>>>>("RegisterEvents")?.Invoke(mod, play);

    public static bool DoPrefix(string mod, Func<object> getEvent) =>
        Bind<Func<string, Func<object>, bool>>("DoPrefix")?.Invoke(mod, getEvent) ?? true;

    public static IDisposable Unrecorded() => Bind<Func<IDisposable>>("Unrecorded")?.Invoke() ?? Nothing.Instance;

    public static void AddAddOn(string nameAndVersion) => Bind<Action<string>>("AddAddOn")?.Invoke(nameAndVersion);

    public static bool AddHook(string name, Delegate handler) => Bind<Func<string, Delegate, bool>>("AddHook")?.Invoke(name, handler) ?? false;

    private class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();
        public void Dispose() { }
    }
}
```

Bind the rest of the methods below the same way, with the delegate type matching the signature in `ModBridge.cs`. Every fallback is what single player would do: `IsMultiplayer` is false, `DoPrefix` returns true so the caller just does the thing, `Unrecorded` returns something harmless to dispose, `AddHook` returns false.

List BeaverBuddies under `RequiredMods` in your manifest (`{ "Id": "beaverbuddies" }`) so it loads first and the type is there when you first look it up. Leave it out if your mod should also run without it.

## What the game is doing

| Method | What it tells you |
|---|---|
| `IsMultiplayer()` | A multiplayer game is running |
| `IsHost()` | This machine hosts it |
| `IsLoaded()` | The game finished loading and events can flow |
| `IsTicking()` | The simulation is in the middle of a tick |
| `IsReplayingEvents()` | An event is being played right now |
| `CanRecord()` | An event sent now would go out |
| `ShouldPlayPatchedEvents()` | Whether a patched method that sent an event still runs on this machine |
| `LocalPlayerID()` | This player's ID, the same across restarts and on every machine |
| `LocalPlayerName()`, `LocalPlayerColor()` | What other players see |
| `Log`, `LogWarning`, `LogError` | BeaverBuddies' own log, so your lines land next to its event log in Player.log |

`LocalPlayerID()` is what you store when something belongs to a player: who owns a building, a district, a stockpile of points.

## Sending your own events

Anything a player does that changes the game has to be an event. Clicking a button, giving an order, changing a setting on a building: send it, don't apply it.

**The event.** Any class Newtonsoft.Json can round-trip: public fields, a parameterless constructor, `[Serializable]`. BeaverBuddies sends it as JSON with its type name, so the same class has to exist on every machine. Refer to entities by their `EntityId` as a string, never by reference.

**Sending it.** Call `DoPrefix(mod, getEvent)` where the player acted. It returns whether you should go on and apply the change here too: true in single player, before the game has loaded, while events are already playing, or when this machine plays its own events at once. If it returns false, the event went out and will come back to you to play.

```csharp
[Serializable]
public class OpenGateEvent
{
    public string entityID;
    public bool open;
}

// The panel's button
public static void SetOpen(Gate gate, bool open)
{
    string entityID = gate.GetComponent<EntityComponent>().EntityId.ToString();
    if (BeaverBuddiesBridge.DoPrefix("mymod", () => new OpenGateEvent { entityID = entityID, open = open }))
        gate.SetOpen(open);
}
```

The same call works as a Harmony prefix on a game method the player triggers: return its result from the prefix, and the original runs only when it should.

**Playing it.** Register once at startup, with the same mod ID you send with:

```csharp
BeaverBuddiesBridge.RegisterEvents("mymod", (payload, playerID, singleton) =>
{
    if (payload is OpenGateEvent e && Guid.TryParse(e.entityID, out Guid id))
    {
        var registry = (EntityRegistry)singleton(typeof(EntityRegistry));
        registry.GetEntity(id)?.GetComponent<Gate>()?.SetOpen(e.open);
    }
});
```

BeaverBuddies calls it on every machine, on the same tick, with the event, the ID of the player who sent it, and `singleton(typeof(T))` to get the game's singletons (an `EntityRegistry` to look up the entity by ID, for one). Check the entity still exists and the player is still allowed to do it: the world may have moved on between the click and the tick.

With more than a couple of events, give them a base class with an abstract `Replay` method, the sender's `playerID` and the entity lookups, and have the registered callback set `playerID` and call `Replay`. That's the shape of BeaverBuddies' own `ReplayEvent`, so its events in `Events/` read as examples.

**Game logic that calls patched methods.** If your tick code calls a game method you (or BeaverBuddies) patched to send an event, wrap the call in `using (BeaverBuddiesBridge.Unrecorded())`. Inside it the method runs as is on every machine instead of sending an event, which is what you want from logic every machine is already running.

## Staying in sync

Every machine has to make exactly the same changes, in the same order. That comes down to a few rules.

**Change state only on ticks or in events.** Ticks are `TickableComponent.Tick`, `ITickableSingleton.Tick` and anything they call. Never in `Update`, `UpdateSingleton`, `LateUpdate`, a UI callback or a coroutine: frames run at a different rate on every machine. A UI callback sends an event, the event changes the state.

**Randomness comes from the game.** Use Timberborn's `IRandomNumberGenerator` (or `UnityEngine.Random`) during ticks and events: BeaverBuddies seeds it the same on every machine. Never `System.Random`, never `Guid.NewGuid` for anything that matters outside a tick. If you need a stable "random" pick that every machine agrees on, hash something they all share, like an entity ID.

**Visuals can do whatever they like.** A MonoBehaviour that moves a model, plays an animation or draws an overlay changes nothing the simulation reads, so it's free to run per frame. Keep that line sharp: if a visual starts deciding things (an explosion that damages what it touches), that decision belongs in a tick.

**Only the tick decides who's where.** Read positions, health and the like from your components, not from where a model happens to be drawn this frame.

**Saved state.** Save what you add (`IPersistentEntity`, `ISaveableSingleton`) like any Timberborn mod. Clients load the host's save when they join, so anything not saved isn't on their machine.

## Visuals in multiplayer

Ticks don't arrive evenly in multiplayer. A client that runs out of ticks from the host stops the game's clock (it sets the game speed to 0) and then runs faster to catch up, several times a second. Anything timed by `Time.time` or `Time.deltaTime` freezes and jumps along with it.

For visuals that should keep moving smoothly (things drifting on water, a cart sliding, a bobbing float), use `Time.unscaledTime` and `Time.unscaledDeltaTime`, and smooth toward the simulated position instead of snapping to it. Taking an object's speed from a single tick's step makes it surge; average it over the last second or so of steps and ease toward where it should be by now.

Something that should stop when the player pauses the game (an arrow in flight, a machine's swing) can stay on game time. It'll stutter the same way characters do.

## Hooks

Add these once, when your mod starts, with `AddHook(name, delegate)`. Cast the lambda to the delegate type the hook takes, or `AddHook` can't tell what it is:

```csharp
BeaverBuddiesBridge.AddHook("CanPlaceOn", (Func<IEnumerable<Vector3Int>, string, bool>)((tiles, playerID) => MyClaims.Allows(tiles, playerID)));
```

It returns false and logs a warning for a name it doesn't know or a delegate of the wrong type. Every hook combines with every other mod's: any false refuses. The full list, with each delegate type, is at the top of `ModBridge.cs`.

| Hook | What it's for |
|---|---|
| `CanSend` `(event, playerID) => bool` | Keeps a player's event (BeaverBuddies' own too) from going out, like orders to a building they don't own |
| `CanPlay` `(event, playerID) => bool` | Drops an event on every machine when its player may not do it. It can also trim the event's fields. Runs on every machine, so a modified client can't get around it |
| `PlayScope` `(event, playerID) => IDisposable` | Opened around each event while it plays, to charge a cost to its player, for one |
| `HostsAlone` `settings => bool` | Whether a game started alone is still hosted, so everything goes through events: a game against players your mod runs |
| `CanPlaceOn` `(tiles, playerID) => bool` | Whether a player may build on these tiles. Land claims are this |
| `PlaceInstead` `(spec, builder, placement, playerID) => bool` | Places a building some other way. Return true when you did |
| `PlaceScope` `playerID => IDisposable` | Opened around the game placing a building for a player |
| `Placed` `(placement, playerID) => void` | After a player's building was placed, on every machine |
| `Refused` `(spec, placement, playerID) => void` | A player's building that couldn't be placed, on every machine |
| `DistrictOwner` `district => playerID`, `PlayerName` `playerID => name` | Who owns a district: only its owner sets up its side of a Trading Post, Power Exchange or toll station |
| `ShowCursorAt` `world => bool` | Hides another player's cursor somewhere the local player can't see |
| `UnlocksToolsHere` `templateName => bool` | Whether a building one player unlocked unlocks its tool on this machine, for per-player research |
| `SetWorkingHoursInstead` `(playerID, hours) => bool` | Sets a player's working hours some other way, for per-player schedules |
| `MatchStarting` `() => void` | Before a match's game starts on this machine |
| `PlayersPlaceStart` `() => bool` | Each player in a match places their own start: the game starts with no starting building, for your mod to found each settlement where it's placed |
| `NewGamePlacesStart` `() => bool` | The same for a new game outside a match |
| `MapStarts` `placements => void` | Where the map's starting locations were, in player order, as a game with no starting building is created. Called before it's saved, so keep them in your save |
| `StartWarning` `() => string` | A reason to ask the host before the game first runs, or null |
| `StrandedFaction` `beaver => faction` | The faction a beaver with no district keeps, or null |

Hooks get your own events as you sent them and BeaverBuddies' events as they are, so a hook can tell them apart by type.

These are methods, not hooks: `AddAddOn(nameAndVersion)` names your mod and version (players who join with a different set get a warning, since they'd fall out of sync), `CanPlace(tiles, playerID)` asks every mod's placement rules at once, `PlaceFor(placers, spec, placement, playerID)` places a building for a player from tick logic, sending nothing, and `AddMultiplayerMode(modeId, mapStartsPerPlayer, newGamePlacesStart)` says how new games in one of your game modes start.

## Game modes and settings

Game modes and game settings are the Game Modes mod's (`GameModes.Modes`, `GameModes.GameSettings`). Reference its DLL and add yours as your mod starts. BeaverBuddies ships Co-op (`bb.coop`), and Timber Empires adds PvE, PvP and Horde. Matchmaking only pairs players who picked the same mode, and their settings travel with their search.

## Factions

Every game mixes factions: each player builds with their own, alone too. A guest picks theirs in the lobby, before the host starts, or with the My faction setting; the host's is the game's. Every faction installed loads in every game, and each player's toolbar shows only their own faction's buildings.

A faction mod made of content alone (blueprints, models, textures) works as it is, as long as every player has it. BeaverBuddies notes every faction the game loads and warns a player who joins without one the host has. Matchmaking only pairs players with the same factions.

Ask about factions with `LocalFaction()`, `PlayerFaction(playerID)`, `TemplateFaction(templateName)`, `FactionHas(templateName, faction)` and `FactionOf(entity)`: a beaver's or bot's is its district's, a building's is its template's, else its district's. `NeedAllowed` and `GoodAllowed` say whether a faction's beavers have a need or keep a good. A mod that adds buildings to a faction (Timber Empires does) lists them in a blueprint of its own, which the game only reads with BeaverBuddies there:

```json
{
  "FactionCollectionsSpec": {
    "FactionId": "Folktails",
    "Collections": [ "MyMod.Buildings.Folktails" ],
    "RequiredMod": "archipelago"
  }
}
```

`RequiredMod` is optional: with it, the collections only load when that mod is enabled too, for buildings made of its specs.

## Testing alone

`Tools/FakePlayer` joins a game you host as another player, with a name and a color, and moves a cursor around. It doesn't run the game, so it won't catch desyncs, but it's enough to test anything that depends on there being other players: ownership, per-player rules, the player list, cursors.

```
dotnet run --project Tools/FakePlayer -- [seconds] [name] [hexColor] [host] [port]
```

The same name always gets the same player ID, so what you give it stays its own across runs.

Desyncs need a second machine running the game. With debug mode on, BeaverBuddies traces each tick and reports the first one where the machines disagree. A desync that shows up right after one of your events usually means something in it ran outside a tick, or read state that's only on one machine.

## Checklist

1. Bind the bridge, fall back to single player when it's missing, and add your hooks with `AddHook`.
2. Call `AddAddOn` with your mod's name and version.
3. Register your events and send every player action through `DoPrefix`.
4. Keep every state change in ticks or events, and use the game's random.
5. Run smooth visuals on unscaled time.
6. Test with the fake player, then on two machines with debug mode on.
