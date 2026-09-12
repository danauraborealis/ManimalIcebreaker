using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;

// Isolated feasibility probe against the installed SPT assemblies. No server,
// player profile, core database, or mod runtime is changed.
var table = Activator.CreateInstance<LocationTable>();
int ordinal = 1;
foreach (var property in typeof(LocationTable).GetProperties())
{
    if (property.PropertyType == typeof(Location))
        property.SetValue(table, new Location
        {
            Base = new LocationBase
            {
                Id = property.Name,
                IdField = ordinal++.ToString("x24"),
                Enabled = false
            }
        });
    else if (property.Name == "Base")
        property.SetValue(table, Activator.CreateInstance(property.PropertyType));
}

var locations = table.GetDictionary();
int originalCount = locations.Count;
var suburbs = table.GetLocation("suburbs");
const string probeId = "ffffffffffffffffffffff01"; // Test fixture only, not a release identity.
var icebreaker = new Location
{
    Base = new LocationBase { Id = "icebreaker", IdField = probeId, Enabled = true }
};
locations.Add("icebreaker", icebreaker);

Check(ReferenceEquals(locations, table.GetDictionary()), "GetDictionary returns persistent storage");
Check(ReferenceEquals(icebreaker, table.GetLocation("icebreaker")), "new lowercase location resolves");
Check(ReferenceEquals(icebreaker, table.GetLocation("Icebreaker")), "new mixed-case location resolves");
Check(ReferenceEquals(suburbs, table.Suburbs) && ReferenceEquals(suburbs, table.GetLocation("suburbs")),
    "Suburbs remains the original independent location");
var response = new LocationController(null!, table, null!).GenerateAll("ffffffffffffffffffffff02");
Check(response.Locations!.Count == originalCount + 1, "GenerateAll includes one additional map");
Check(ReferenceEquals(response.Locations[probeId], icebreaker.Base), "client map list uses the new unique ID");
Check(response.Locations.ContainsKey(suburbs!.Base!.IdField), "client map list also retains Suburbs");
Console.WriteLine("Server location registration feasibility proven; no live raid was tested.");

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}
