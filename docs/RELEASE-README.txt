Manimal-Icebreaker
==================

A backport of retail EFT 1.0's Icebreaker map for SPT 4.0.x, reached from the
Woods/Lighthouse hovercraft transit or straight from the map screen (400k
roubles carried in your gear, consumed at raid start).

INSTALL
-------
Extract the zip into your SPT install folder (the one with EscapeFromTarkov.exe
and the SPT folder in it). It merges into BepInEx\plugins and SPT\user\mods
only. The map's scene bundle ships inside the plugin folder (which is why the
zip is large) and is loaded straight from there — nothing is ever written into
EscapeFromTarkov_Data.

Updating from 1.0.x: earlier builds copied the scene bundle into
EscapeFromTarkov_Data\StreamingAssets on first launch. This version loads from
the plugin folder instead and deletes those leftovers itself at startup (it
removes only files it put there) — no reinstall needed, just replace the mod.

For Fika: every player extracts the zip into their own install; the SPT\ half
only matters on the machine that runs the server.

REQUIRED MODS
-------------
  - tarkin's spt-ladders        (the ice-intro rope ladder is climbed through it)
  - WTT-CommonLib               (client AND server halves)
  - the Black Division bots mod (supplies the blackdiv bot roles the crew uses)

FIKA (CO-OP)
------------
The mod is Fika-aware (written against Fika-Plugin current as of 2026-07):
bots and map events are host-authoritative, fares are charged per player on
their own machine through the replicated inventory path, and map triggers
respond to any member of the group. Every peer must run the SAME Icebreaker
version.

Fika players ALSO need the separate addon zip (Manimal-IcebreakerFika-x.y.z),
extracted the same way. It replicates the custom world state fika can't see:
chain-door plant/breach, sealed doors, the frozen hatch, the heli call, the
blowtorch, ladder climbing, and the cutscene progress-door gate. It only
loads when Fika is installed (hard dependency).

KNOWN CO-OP LIMITATIONS (untested in a live multi-client session):
  - late join / reconnect behavior is unverified (world events fired before a
    player joined are not replayed to them)

Report oddities with your BepInEx\LogOutput.log.

NOTES
-----
  - Scav raids to Icebreaker are intentionally disabled.
  - The heli exfil charges 2400 EUR through the native pay prompt.
  - The map fare / transit fare / raid timer are configurable in
    BepInEx\config\com.manimal.icebreaker.cfg.
