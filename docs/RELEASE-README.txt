Manimal-Icebreaker
==================

A backport of retail EFT 1.0's Icebreaker map for SPT 4.0.x, reached from the
Woods/Lighthouse hovercraft transit or straight from the map screen (400k
roubles carried in your gear, consumed at raid start).

INSTALL
-------
Extract the zip into your SPT install folder (the one with EscapeFromTarkov.exe
and the SPT folder in it). It merges into BepInEx\plugins, SPT\user\mods and
EscapeFromTarkov_Data\StreamingAssets — the last one carries the map itself,
which is why the zip is large.

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

The package includes ManimalIcebreakerFika.dll, a sync addon that replicates
the chain-door plant/breach and sealed-door state between players. It only
loads when Fika is installed (hard dependency) — without Fika it is inert.

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
