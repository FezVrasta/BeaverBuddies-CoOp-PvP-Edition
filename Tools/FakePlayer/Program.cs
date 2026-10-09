using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
using System.Text;
using TimberNet;

// Joins a hosted BeaverBuddies game as an extra player, announces a name
// and color, and moves a cursor in circles so it's easy to spot.
// Usage: FakePlayer [seconds] [name] [hexColor] [host] [port]

int seconds = args.Length > 0 ? int.Parse(args[0]) : 3600;
string name = args.Length > 1 ? args[1] : "Rival";
string color = (args.Length > 2 ? args[2] : "FF3344").TrimStart('#').ToUpperInvariant();
string host = args.Length > 3 ? args[3] : "127.0.0.1";
int port = args.Length > 4 ? int.Parse(args[4]) : 25565;

// Same ID for the same name, so districts stay owned across runs
string playerID = new Guid(MD5.HashData(Encoding.UTF8.GetBytes("BeaverBuddies.FakePlayer." + name))).ToString();

var client = new TimberClient(new TCPClientWrapper(host, port));
client.OnLog += m => { if (!m.StartsWith("Received event")) Console.WriteLine(m); };
bool failed = false;
client.OnError += m => { Console.Error.WriteLine("Error: " + m); failed = true; };
client.OnMapReceived += bytes => Console.WriteLine($"Map received ({bytes.Length / 1024} KB)");
// Nothing to load: the host's resync save only starts the game again from tick 0
client.OnResync += bytes => { Console.WriteLine($"Resync save received ({bytes.Length / 1024} KB)"); client.EndResync(); };

Console.WriteLine($"Connecting to {host}:{port} as {name} ({playerID})...");
client.Start();

var started = DateTime.UtcNow;
bool announced = false;
double angle = 0;
while (!failed && !client.IsStopped && (DateTime.UtcNow - started).TotalSeconds < seconds)
{
    client.Update();
    client.ReadEvents(client.TickCount);

    if (!announced && (DateTime.UtcNow - started).TotalSeconds > 3)
    {
        client.DoUserInitiatedEvent(new JObject
        {
            ["$type"] = "BeaverBuddies.Players.PlayerAnnouncedEvent, BeaverBuddies",
            ["name"] = name,
            ["color"] = color,
            ["ticksSinceLoad"] = client.TickCount,
            ["randomS0Before"] = null,
            ["playerID"] = playerID,
            ["type"] = "PlayerAnnouncedEvent",
        });
        announced = true;
        Console.WriteLine($"Announced {name} with color #{color}. Set a District Center's owner to them now.");
    }

    angle += 0.05;
    client.SendTransientMessage(new JObject
    {
        [TimberNetBase.TYPE_KEY] = "PlayerCursor",
        ["playerID"] = playerID,
        ["color"] = color,
        ["visible"] = true,
        ["x"] = 64 + 10 * Math.Cos(angle),
        ["y"] = 20,
        ["z"] = 64 + 10 * Math.Sin(angle),
    });

    Thread.Sleep(100);
}

Console.WriteLine("Leaving.");
client.Close();
