# Building mods on BeaverBuddies

BeaverBuddies keeps every machine in a game in sync by running the same simulation everywhere: each player's actions go out as events, and every machine plays them on the same tick. A mod that changes the game (new buildings, new orders, new rules) has to play by the same rules or the game falls out of sync.

This is how Timber Empires, the PvP add-on, is built. Everything here is what it does.

## The bridge

A mod doesn't reference BeaverBuddies to compile. `BeaverBuddies.Modding.ModBridge` is a static class whose methods only take and return .NET, Unity and Timberborn types. Your mod finds it by name at runtime and binds each method to a delegate once, so calling it costs no more than a normal call.

If BeaverBuddies isn't installed (or it's too old to have the bridge), the lookup finds nothing and your mod should treat the game as single player. Everything keeps working, it just doesn't sync.

The bridge only grows. Existing methods keep their signatures, new ones get added, and `Version()` goes up when a change can't be avoided.

### Binding it

Copy Timber Empires' `Bridge/BeaverBuddiesBridge.cs` into your mod and change the namespace and the log prefix. It stands alone. The core of it is this:

```csharp
private static Type _type = AppDomain.CurrentDomain.GetAssemblies()
    .Select(a => a.GetType("BeaverBuddies.Modding.ModBridge", false))
    .FirstOrDefault(t => t != null);

private static T Bind<T>(string name) where T : Delegate
{
    MethodInfo method = _type?.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
    return method == null ? null : (T)Delegate.CreateDelegate(typeof(T), method, false);
}

public static bool IsMultiplayer => Bind<Func<bool>>("IsMultiplayer")?.Invoke() ?? false;
public static bool DoPrefix(string mod, Func<object> getEvent) =>
    Bind<Func<string, Func<object>, bool>>("DoPrefix")?.Invoke(mod, getEvent) ?? true;
```

Cache the bound delegates (the real file keeps them in a dictionary). Every fallback is what single player would do: `IsMultiplayer` is false, `DoPrefix` returns true so the caller just does the thing.

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
| `Log`, `LogWarning`, `LogError` | BeaverBuddies' own log, so your lines land next to its event log in error reports |

`LocalPlayerID()` is what you store when something belongs to a player. Timber Empires keys land claims, districts and science on it.

## Sending your own events

Anything a player does that changes the game has to be an event. Clicking a button, giving an order, changing a setting on a building: send it, don't apply it.

**The event.** Any class Newtonsoft.Json can round-trip: public fields, a parameterless constructor, `[Serializable]`. BeaverBuddies sends it as JSON with its type name, so the same class has to exist on every machine. Refer to entities by their `EntityId` as a string, never by reference.

**Sending it.** Call `DoPrefix(mod, getEvent)` where the player acted. It returns whether you should go on and apply the change here too: true in single player, before the game has loaded, while events are already playing, or when this machine plays its own events at once. If it returns false, the event went out and will come back to you to play.

```csharp
public static void Send(MineLauncher launcher)
{
    string entityID = GetEntityID(launcher);
    if (entityID == null) return;
    if (BeaverBuddiesBridge.DoPrefix("mymod", () => new MineReleaseEvent { entityID = entityID }))
        launcher.RequestRelease();
}
```

The same call works as a Harmony prefix on a game method the player triggers: return its result from the prefix, and the original runs only when it should.

**Playing it.** Register once at startup, with the same mod ID you send with:

```csharp
BeaverBuddiesBridge.RegisterEvents("mymod", (payload, playerID, singleton) =>
{
    if (payload is MyEvent e) e.Play(playerID, singleton);
});
```

BeaverBuddies calls it on every machine, on the same tick, with the event, the ID of the player who sent it, and `singleton(typeof(T))` to get the game's singletons (an `EntityRegistry` to look up the entity by ID, for one). Check the entity still exists and the player is still allowed to do it: the world may have moved on between the click and the tick.

Timber Empires wraps all of this in its own `Bridge/ReplayEvent.cs`, a base class with `Replay(context)`, `DoPrefix` and the entity lookups, shaped like BeaverBuddies' own `ReplayEvent`. Copying it gets you the same API BeaverBuddies' events use.

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

Add these once, when your mod starts. Each one combines with every other mod's: any false refuses.

| Method | What it's for |
|---|---|
| `AddAddOn(nameAndVersion)` | Names your mod and version. When players join, BeaverBuddies compares the host's add-ons with theirs and warns if they differ, since a different mod set falls out of sync |
| `AddCanSend(event => bool)` | Keeps the local player from sending an event (BeaverBuddies' own events too). Timber Empires stops players giving orders to another player's buildings this way |
| `AddCanPlay((event, playerID) => bool)` | Drops an event on every machine when the player who sent it may not do it. It can also trim the event's fields. Runs on every machine, so it's the check that can't be bypassed by a modified client |
| `AddPlayScope((event, playerID) => IDisposable)` | Opened around each event while it plays. Timber Empires uses it to charge science to the player who sent the event |
| `AddCanPlaceOn((tiles, playerID) => bool)` | Whether a player may build on these tiles. Land claims are this |
| `CanPlace(tiles, playerID)` | Asks every mod's placement rules at once |
| `AddPlaceInstead((spec, builder, placement, playerID) => bool)` | Places a building some other way. Return true when you did |
| `AddPlaceScope(playerID => IDisposable)` | Opened around the game placing a building for a player |
| `AddPlaced((placement, playerID) => void)` | After a player's building was placed, on every machine |
| `AddRefused((spec, placement, playerID) => void)` | A player's building that couldn't be placed, on every machine |
| `PlaceFor(placers, spec, placement, playerID)` | Places a building for a player from tick logic, sending nothing |
| `AddShowCursorAt(world => bool)` | Hides another player's cursor at a spot. Timber Empires hides cursors in the fog of war |
| `AddUnlocksToolsHere(templateName => bool)` | Whether a building one player unlocked unlocks its tool on this machine, for per-player research |
| `AddSetWorkingHoursInstead((playerID, hours) => bool)` | Sets a player's working hours some other way, for per-player schedules |

Hooks get your own events as you sent them and BeaverBuddies' events as they are, so a hook can tell them apart by type.

## Testing alone

`Tools/FakePlayer` joins a game you host as another player, with a name and a color, and moves a cursor around. It doesn't run the game, so it won't catch desyncs, but it's enough to test anything that depends on there being other players: ownership, per-player rules, the player list, cursors.

```
dotnet run --project Tools/FakePlayer -- [seconds] [name] [hexColor] [host] [port]
```

The same name always gets the same player ID, so what you give it stays its own across runs.

Desyncs need a second machine running the game. With debug mode on, BeaverBuddies traces each tick and reports the first one where the machines disagree. A desync that shows up right after one of your events usually means something in it ran outside a tick, or read state that's only on one machine.

## Checklist

1. Bind the bridge, fall back to single player when it's missing.
2. Call `AddAddOn` with your mod's name and version.
3. Register your events and send every player action through `DoPrefix`.
4. Keep every state change in ticks or events, and use the game's random.
5. Run smooth visuals on unscaled time.
6. Test with the fake player, then on two machines with debug mode on.
