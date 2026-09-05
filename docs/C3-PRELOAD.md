# Preload the C-3 card before late loot insertion

The client inserts the C-3 card into an already spawned rogue's inventory. It is therefore absent from the original profile resource prewarm. DoorInteractState creates the card synchronously in the player's hand. An unloaded prefab throws before the state's `_spawned` flag is set, repeating the failed operation every frame and stalling the interaction.

C3KeycardSweep now awaits LoadBundlesAndCreatePools using the card's Prefab and the Raid pool before making its loot rolls. The raid pool keeps the resource available for later use. A failed or cancelled load logs an error and skips injection instead of giving the bot an unusable item. Drop probability and door logic are unchanged.

The client builds against the SPT 4.1.3 assembly set (EFT 0.16.9.5.40743). The reported failing raid used SPT 4.1.4 with the same EFT version. Installed DLL hashes were verified; a new C-3 door interaction must still be tested in-game. Multiplayer is not validated.
