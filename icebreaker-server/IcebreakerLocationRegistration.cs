using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace Manimal.Icebreaker.Server;

public static class IcebreakerLocationRegistration
{
    public const string Key = IcebreakerLocation.Key;
    public const string Id = IcebreakerLocation.Id;

    public static void EnsureAvailable(LocationTable table)
    {
        var locations = table.GetDictionary();
        if (locations.ContainsKey(Key))
            throw new InvalidOperationException($"Icebreaker location key '{Key}' is already registered.");
        foreach (var pair in locations)
            if (pair.Value?.Base is { } existing &&
                (existing.IdField.ToString() == Id || IcebreakerLocation.Matches(existing.Id)))
                throw new InvalidOperationException($"Icebreaker location identity is already used by '{pair.Key}'.");
    }

    public static void Register(LocationTable table, Location location)
    {
        if (location.Base?.Id != Key || location.Base.IdField.ToString() != Id)
            throw new InvalidOperationException("Icebreaker base identity does not match Directory.Build.props.");
        EnsureAvailable(table);
        // SPT 4.1.5 returns persistent storage, consulted by GetLocation and GenerateAll.
        table.GetDictionary().Add(Key, location);
    }
}
