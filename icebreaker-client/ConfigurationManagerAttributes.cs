using System;

// CONFIGURATION MANAGER ATTRIBUTES
//
// this is deliberately NOT a reference to the ConfigurationManager plugin. its config
// browser reads the tag object off a ConfigDescription by DUCK TYPING — it reflects over
// whatever object it finds looking for fields with these exact names — so every mod ships
// its own copy of the class rather than taking a hard dependency. keeping the file
// verbatim (public fields, this class name, no namespace) is what makes that work; renaming
// a field just makes the browser silently ignore it.
//
// only the members we actually use are declared. the browser tolerates a partial class.
#pragma warning disable 0169, 0414, 1591
internal sealed class ConfigurationManagerAttributes
{
    // hidden behind the browser's "Advanced settings" toggle. this is where the tuning and
    // developer knobs live: still reachable when talking someone through a fix, but not in
    // the face of a player who just wants to turn the blizzard off.
    public bool? IsAdvanced;

    // not drawn at all. reserved for entries that would actively mislead if a player
    // touched them; nothing currently uses it, but the browser looks for it by name.
    public bool? Browsable;

    // ordering within a section. higher shows first.
    public int? Order;

    public string Category;
}
#pragma warning restore 0169, 0414, 1591
