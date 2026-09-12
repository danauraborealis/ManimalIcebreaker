#nullable enable
using System;

// Compiled into the client and server, with constants generated from the same props.
// Kept internal so the optional Fika addon uses the main client's raid/fare bridge.
namespace Manimal.Icebreaker
{
    internal static class IcebreakerLocation
    {
#if ICEBREAKER_SERVER
        internal const string Key = Server.BuildInfo.LocationKey;
        internal const string Id = Server.BuildInfo.LocationId;
#else
        internal const string Key = BuildInfo.LocationKey;
        internal const string Id = BuildInfo.LocationId;
#endif
        internal static bool Matches(string? value)
            => string.Equals(value, Key, StringComparison.OrdinalIgnoreCase);
    }
}
