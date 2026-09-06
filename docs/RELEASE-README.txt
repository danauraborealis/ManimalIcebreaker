Manimal-Icebreaker
==================

A backport of retail EFT 1.0's Icebreaker map for SPT 4.1.5+, unlocked on the
map screen by completing Boreas - Part 3 (400k roubles carried in your gear,
consumed at raid start).

INSTALL
-------
Extract the zip into your SPT install folder (the one with EscapeFromTarkov.exe
and the SPT_Runtime folder in it). It merges into BepInEx\plugins and SPT_Runtime\user\mods
only. The map's scene bundle ships inside the plugin folder (which is why the
zip is large) and is loaded straight from there — nothing is ever written into
EscapeFromTarkov_Data.

Updating from 1.0.x: earlier builds copied the scene bundle into
EscapeFromTarkov_Data\StreamingAssets on first launch. This version loads from
the plugin folder instead and deletes those leftovers itself at startup (it
removes only files it put there) — no reinstall needed, just replace the mod.

If your server runtime is in a different folder, move the contents of
SPT_Runtime\user\mods into that server's user\mods folder.
This build requires SPT 4.1.5 or a later 4.1 patch; it does not support SPT 4.0.

For Fika: every player extracts the zip into their own install; the SPT_Runtime\ half
only matters on the machine that runs the server.

REQUIRED MODS
-------------
  - tarkin's spt-ladders        (the ice-intro rope ladder is climbed through it)
  - WTT-CommonLib 3.0.6+        (client AND server halves)
  - WTT-Content Backport 2.0.1+ (client AND server halves)
  - Black Division 1.3.1+      (client, server, and prepatcher)
  - MoreBotsAPI 2.1.1+         (client, server, and prepatcher)
  - BigBrain 1.5.0+
  - SAIN 4.5.1                (SPT 4.1 client AND server halves)
  - Manimal's CS Gas 2.0.0+   (client AND server halves)

Use the SPT 4.1 releases of all dependencies. Server dependency ranges allow
patch updates within the listed minor versions.

FIKA (CO-OP)
------------
The SPT 4.1 co-op addon has not yet been built or tested against a compatible
Fika installation. It is excluded from the default release build. Do not
reuse the SPT 4.0 addon DLL with this release.

The existing co-op integration is designed so that
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
  - The map fare and raid timer are configurable in
    BepInEx\config\com.manimal.icebreaker.cfg.
