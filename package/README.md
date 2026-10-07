# LethalMimics

Enemies use your crew's voices to lure them in. Players record short voice clips, and enemies play them back as that player so they sound like teammates who aren't there.

> **Everyone in the lobby needs this mod.**

---

## How it works

- **Hold `J`** to record and let go to stop. Clips are up to 8 s long.
  - Your normal voice chat is muted while you hold the key, so nobody hears you live. Enemies don't hear it either.
  - Clips are saved on your PC and uploaded again each time you join a lobby.
- **Fully automatic.** During a round, the mod decides which enemies speak, when, whose voice they use and how each clip is varied. Nobody has to do anything; the host's game makes the decisions so everyone hears the same thing. Every player hears it from that enemy at the same moment. **The person whose voice it is hears nothing.**
- **Which enemies mimic:** each level rolls a share of its enemies, from about half up to all of them, so some levels have a few mimics and some have every enemy talking. Several enemies can be mimicking at once in different parts of the level.
- **Whose voice gets used:**
  - Players standing close to the enemy are skipped. Players who are farther away or in another area (inside vs outside) are more likely to be picked.
  - Dead players' voices can be picked too.
  - An enemy only speaks if someone other than the voice's owner is close enough to hear it.
- **Variety:**
  - Clips are picked at random, and clips played recently are avoided.
  - Each enemy waits a random time between mimics. After you hear one, other enemies near you hold off for a few seconds, so mimics don't talk over each other.
  - Each playback gets a small change in pitch and volume.
  - Sometimes only part of a clip plays.
  - Sometimes the enemy follows up with a repeat ("hey… hey!").
- **Sounds real:** mimic voices fade with distance and are muffled through walls just like real voices. They also follow each listener's per-player volume slider.

## Hosts with the AI add-on

There is a separate, experimental **LethalMimics AI** add-on that clones voices with AI models on the host's own PC. It is **not** part of this package and isn't on Thunderstore; hosts who want it can get it from [GitHub](https://github.com/BKPatt/LethalMimics) at their own risk. You don't need it to play.

If you join a host who runs it with AI switched on:

- **You get an on-screen notice.** Press `Delete` (`DismissNoticeKey`) to hide it.
- **Your voice chat is sent to the host's PC** so enemies there can learn to speak as you. Only speech other players could already hear is sent: nothing while push-to-talk is released, and nothing while you record a clip. It stops as soon as the host switches the AI off.
- **To opt out**, set `ShareVoiceWithHostAi = false`. Your voice is then never sent to the host or cloned.

With a normal host (no add-on), nothing is ever sent beyond your recorded clips.

---

## Config (`BepInEx/config/brant.LethalMimics.cfg`)

| Section | Who it affects | Highlights |
|---|---|---|
| Recording | each player | `RecordKey`, `MaxClipSeconds`, `SaveClips`, `ClearClipsKey`, `ShowHud`, `DismissNoticeKey` |
| Mimics (host only) | lobby | intervals, chances, `MinMimicShare`, `ListenerCooldown`, `HearingRange`, `MinOwnerDistance`, `ExcludedEnemies` |
| AI Voice Sharing | each player | `ShareVoiceWithHostAi` (opt out of hosts running the AI add-on) |

- **Keys** use Unity Input System paths, such as `<Keyboard>/j` or `<Mouse>/middleButton`. The defaults (`J`, `Delete`) avoid the keys used by the base game and by popular mods like TooManyEmotes and ControlCompany.
- **Saved clips** live in `%LOCALAPPDATA%\LethalMimics\clips`, outside your mod profile, so exporting or sharing a profile never includes your voice. Set `ClearClipsKey` to get a key that deletes all of your clips (yours and everyone's copies).

## Troubleshooting
- **"No microphone audio captured":** check that your mic is enabled in the game's settings.
- **Enemies never mimic:** someone needs to have recorded a clip, and a player other than the clip's owner has to be near the enemy. Excluded enemies (bees, Manticoils by default) never mimic.
