# plugin-data — repo backup of the client's authored data sidecars

these files' CANONICAL home is the live dev deploy
(`D:\SPTDev\BepInEx\plugins\ManimalIcebreaker\`) — that is what the release packager
harvests and what raids actually load. this folder is a BACKUP so the repo carries
everything needed to rebuild an install if the deploy dir is ever lost.

excluded on purpose: dlls (built from source), *.bundle (rebaked from the SDK),
streamingassets/ (the ~1.8GB scene payload), dumps/.

refresh after changing any sidecar in the live deploy:

    python -c "import shutil,os; [shutil.copytree(r'D:/SPTDev/BepInEx/plugins/ManimalIcebreaker/'+d, 'icebreaker-client/plugin-data/'+d, dirs_exist_ok=True) for d in ('acoustics','aibake','aiplaces','camera','culling','flares','weather')]"

(or just re-run the copy the assistant used — see git log for this folder.)

restore = copy the folder contents over BepInEx/plugins/ManimalIcebreaker/.
