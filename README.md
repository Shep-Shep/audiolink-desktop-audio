# AudioLink Desktop Audio

[![Add to VCC](https://shep-shep.github.io/vpm/badges/add-to-vcc.svg)](https://shep-shep.github.io/vpm/) [![Buy me a coffee](https://shep-shep.github.io/vpm/badges/buy-me-a-coffee.svg)](https://shepshep.gumroad.com/coffee)

An editor testing tool for Unity. In Play mode, AudioLink reacts to whatever Windows is playing, so you can test audio-reactive work with Spotify, a browser or a DJ app instead of a clip or video player.

## Requirements

- Windows and the Unity editor. Built and tested with Unity 2022.3.
- AudioLink in the project. The tool finds AudioLink by type name, so it works with the VPM package or an imported copy.

## Install

1. Open the [Shep Shep package listing](https://shep-shep.github.io/vpm/) and press **Add to VCC**. ALCOM opens the same button when its setting for VCC links is on.
2. In your project's Manage Project page, add **AudioLink Desktop Audio**.

To work on the tool itself, add this repository's folder as a user package in ALCOM or the Creator Companion instead.

## Use it

1. **GameObject > AudioLink > Add Desktop Audio to Scene** (also under **Tools > AudioLink**, and **Add Component > AudioLink > AudioLink Desktop Audio**), next to AudioLink's own items. The object is tagged EditorOnly, so it never ships in a build or upload. The AudioLink field fills in with the scene's AudioLink. If you add AudioLink later, press **Find AudioLink in Scene** in the Inspector.
2. Enter Play mode with music playing.
3. Allow Unity if your antivirus asks about microphone access. Desktop capture uses the same Windows capture API, so Kaspersky and similar tools ask.

The Inspector shows status, the capture device, level meters for the desktop and AudioLink's input, and approximate latency.

## What it does

- Finds the first AudioLink in the scene (or the one you assign), adds its own audio source to this object and points AudioLink at it for Play mode. AudioLink's original source is left untouched and reconnected when the component is disabled. The AudioLinkAvatar prefab's video player autoplays a default YouTube link into that original source and can claim it, which is why it is not shared.
- In UdonSharp worlds the field cannot be redirected at runtime, so there it borrows AudioLink's own source and restores its settings afterwards.
- VRChat's ClientSim removes EditorOnly objects when Play mode starts. The tool untags its own Play mode copy first, so it keeps running. The saved scene keeps the tag.
- Captures the default Windows output device with WASAPI loopback and resamples it to Unity's output rate.
- Mutes Unity's own audio output while it runs, so the capture cannot pick Unity up again and echo.
- Feeds the source one of two ways. **Filter carrier:** a filter writes the audio into a silent looping clip; lowest delay, but Unity does not always include filter output in `GetOutputData`, which AudioLink reads. **Live clip carrier:** the audio is written into the looping clip just ahead of the play position; AudioLink always sees it, at roughly one editor frame of extra delay. The check tries the filter first and falls back to the clip automatically.
- **Auto level** (on by default): the capture receives each app's audio after its own volume slider, so a quiet app gave AudioLink too little to react to. The tool raises the desktop audio until its loudest recent peak is close to full level, by up to +40 dB. It reacts instantly when the sound gets louder, and eases back up over about 10 seconds when it gets quieter, so short breakdowns stay quieter. The boost never exceeds the feedback margin measured by the safety check. The Inspector shows the current boost; the Desktop meter shows the level before it.

## Feedback safety check

Before feeding, it plays a quiet 997 Hz test tone (-30 dBFS) with Unity muted. It passes only if AudioLink can see the tone and the tone is not in the loopback capture. It tries the filter carrier with the listener filter mute, then the live clip carrier with each mute, and uses the first that passes. If AudioLink saw nothing at all, it retries once. If nothing passes, it refuses to feed and says why.

Report: `%TEMP%/audiolink-desktop-audio-check.txt`, also via **Tools > AudioLink > Open Desktop Audio Check Report**. Newest run first, older runs kept below. Each run lists every audio listener with its scene, whether the source was playing, and whether Game view audio was muted.

## Latency

No fixed delay buffer. The capture keeps a small cushion against uneven timing and trims anything above it every half second. The cushion starts at 4 ms. After a half second with gaps it grows by up to 10 ms, to at most 60 ms; after ten seconds without gaps it shrinks by 1 ms. The live clip carrier reads once per editor frame, so it settles on a larger cushion than the filter carrier. The Inspector shows the current buffer and the total gaps since Play started. After a stall it jumps straight to current audio. Simulated with realistic timing: newest audio reaches AudioLink about 12 ms after Windows mixes it, plus Unity's own audio block (DSP Buffer Size in Project Settings > Audio; 1024 frames is about 21 ms at 48 kHz). The live clip carrier adds one editor frame plus a small margin on top, because clip data can only be written from the main thread.

## Limits

- Windows and Play mode only. AudioLink itself does not run in Edit mode.
- Unity's own audio is silent while this runs.
- In UdonSharp worlds, a video player driving the borrowed source takes it back; the Inspector reports this.
- Two audio listeners (for example a second loaded scene with its own camera) produce Unity's console warning every frame. The tool mutes both, but the scene setup should have one.
- Multichannel output uses the front left and right channels only.

## Files

- `Runtime/DesktopAudioCapture.cs`: loopback capture, ring buffer, carrier feed and tone detector. Plain C#, no Unity dependency.
- `Runtime/AudioLinkDesktopAudio.cs`: the component: AudioLink lookup, source takeover, safety check and status.
- `Runtime/DesktopAudioCarrier.cs`, `Runtime/DesktopAudioListenerMute.cs`: small helpers added at runtime.
- `Editor/AudioLinkDesktopAudioEditor.cs`: Inspector, menus and restoring the listener volume after Play mode.
- Two assembly definitions: `ShepShep.AudioLinkDesktopAudio` (runtime) and `ShepShep.AudioLinkDesktopAudio.Editor` (editor only).

There is no compile-time AudioLink reference. AudioLink is found by type name when Play mode starts.

## Releasing

Raise `version` in `package.json`, commit, and push a tag with the same version, for example `0.1.1`. The Build Release workflow zips the package and publishes the release. Then run the Build Repo Listing workflow in `Shep-Shep/vpm`, or push to its main branch, so the listing picks it up.

## License

MIT. See `LICENSE.md`.
