using System.Text.Json;
using SPTarkov.Server.Core.Models.Eft.Match;

namespace Manimal.Icebreaker.Server;

// Runtime state, never release content. Keep the original path and JSON shape so
// upgrades retain crossings earned by existing profiles.
public sealed class IcebreakerVisitLedger(string path)
{
    private sealed class Entry
    {
        public int Visits { get; set; }
        public Dictionary<string, int> Marks { get; set; } = new();
        public HashSet<string> Raids { get; set; } = new();
    }

    private readonly object gate = new();
    private Dictionary<string, Entry>? profiles;

    private Dictionary<string, Entry> Load()
    {
        if (profiles != null) return profiles;
        var loaded = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path))
        {
            string raw = File.ReadAllText(path);
            if (raw.TrimStart().StartsWith("["))
                foreach (var id in JsonSerializer.Deserialize<List<string>>(raw) ?? [])
                    loaded[id] = new Entry { Visits = 1 };
            else
                foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, Entry>>(raw) ?? [])
                    loaded[pair.Key] = pair.Value;
        }
        return profiles = loaded;
    }

    public bool HasVisited(string profileId)
    {
        lock (gate) return Load().TryGetValue(profileId, out var entry) && entry.Visits > 0;
    }

    public bool HasVisitedSince(string profileId, string questId)
    {
        lock (gate)
            return Load().TryGetValue(profileId, out var entry)
                && entry.Marks.TryGetValue(questId, out var mark) && entry.Visits > mark;
    }

    public void MarkQuestFinished(string profileId, string questId)
    {
        lock (gate)
        {
            var entry = Get(profileId);
            if (!entry.Marks.TryAdd(questId, entry.Visits)) return;
            Save();
        }
    }

    // Each profile in a Fika raid earns its own visit; a retried end request does
    // not create another crossing for that profile.
    public int? RecordRaidEnd(string profileId, EndLocalRaidRequestData? request)
    {
        // SPT 4.1 uses "{location}.{side} {timestamp}". Any outcome counts,
        // including death/MIA; an aborted load without Results does not.
        if (request?.Results == null || string.IsNullOrEmpty(request.ServerId)) return null;
        int separator = request.ServerId.IndexOf('.');
        if (separator < 0 || !IcebreakerLocation.Matches(request.ServerId[..separator])) return null;
        return Record(profileId, request.ServerId);
    }

    public int? Record(string profileId, string raidId)
    {
        lock (gate)
        {
            var entry = Get(profileId);
            if (!entry.Raids.Add(raidId)) return null;
            entry.Visits++;
            Save();
            return entry.Visits;
        }
    }

    private Entry Get(string profileId)
    {
        var data = Load();
        if (!data.TryGetValue(profileId, out var entry)) data[profileId] = entry = new Entry();
        return entry;
    }

    private void Save()
    {
        // Called under gate: serialize a consistent snapshot and replace atomically.
        // A failed write must not become an in-memory-only visit/mark.
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(profiles));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch
        {
            profiles = null;
            throw;
        }
    }
}
