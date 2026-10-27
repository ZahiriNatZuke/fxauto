# Tuned presets

This page summarises the research behind the per-device preset sets that were built alongside FxAuto, for a
**Logitech G435** wireless headset and a pair of **Edifier R12U** desktop speakers. They are worked examples of how to
build presets for your own devices with FxSound's controls, not values to copy blindly: every device, room and pair
of ears is different, so treat them as a starting point and adjust by ear.

FxAuto itself does not ship or require any of these presets. It only switches between the presets you have in
FxSound.

## The idea: device correction + category adjustment

Every preset in a set is built in two layers:

1. **Device correction.** The same base for every preset of a device. It compensates for the device's own frequency
   response (measured where a measurement exists, estimated from the specifications where it does not). This base is
   the device's `General` preset.
2. **Category adjustment.** Small changes on top of the base for what is playing: neutral for Music, intelligibility
   for Video and Voice, level control for live streams, body and dynamics for movies, footstep clarity and no stereo
   widening for competitive shooters, impact for action games, small details for horror, and so on.

Some rules hold for every preset:

- **Small moves.** No band goes above +3 dB, and the sum of the positive bands stays at +6 dB or less, so there is
  headroom and FxSound does not clip. Cutting is preferred to boosting.
- **Effects are used sparingly.** FxSound's effects are a harmonic exciter (*Clarity*), a delay-based stereo widener
  (*3D Surround*, not HRTF), a room reverb (*Ambience*), a maximiser/limiter (*Dynamic Boost*) and a fixed bass boost
  centred on 90 Hz (*Bass Boost*). Ambience is off everywhere: games and films already carry their own reverb, and on
  headphones added reverb smears dialogue and positioning. Surround is off wherever locating sounds matters.
- **Volume leveling** is used only where loudness jumps between sources (videos, live streams, TV, calls, late-night
  movies), and kept off for music, regular films and games so that dynamics and footstep levels stay intact. FxSound
  keeps volume leveling as one global setting; FxAuto stores a value per preset and sends it with the preset.

## Logitech G435 headset

**Measurement.** The correction comes from the RTINGS measurement of the G435 (HMS II.3 rig) as processed by
AutoEq, against AutoEq's target for that rig (Harman-like over-ear: a few dB of bass shelf and ear gain around
3 kHz). Averaged around each FxSound band, the headset compared with the target:

| Band (Hz) | 31 | 62.5 | 116 | 214 | 397 | 735 | 1360 | 2520 | 3000 | 4670 | 6000–7000 | 8640 | 16000 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| G435 vs target (dB) | −4.9 | −0.1 | +1.3 | +1.5 | +0.4 | **−3.4** | −0.4 | **+3.6** | **+3.7** | **−2.9** | −1.1/−1.6 | −1.3 | +2.0 |

- **Bass** is fine from 60 to 400 Hz (slightly full at 116–214 Hz). **Sub-bass rolls off** below 40 Hz; FxSound
  has no band there, so only 62.5 Hz or Bass Boost (90 Hz) can touch it.
- A narrow **dip near 700 Hz**.
- A **peak at 2.5–3 kHz** followed by a **dip at 4–5 kHz**: the most audible problem (slightly shrill voices,
  fatigue).
- Sibilance range (6–9 kHz) slightly low; 16 kHz slightly high.

AutoEq's own fixed-band EQ uses other centre frequencies, so it is not copied as is: the correction is read at
FxSound's frequencies, rounded to 1 dB, and reduced to a half or two thirds where the problem is narrower than an
FxSound band (Q ≈ 1.6), so it does not drag the neighbouring bands.

**Effects on headphones.** Clarity 13 only where some brightness helps (video, sports, MOBA, RPG), 0 for shooters,
racing, action, strategy, horror and voice (the headset already peaks at 3 kHz, and the exciter exaggerates codec
artefacts). Surround at most 13, and only for movies, RPG and sports. Dynamic Boost only for live streams (13) and
Movie Night (25). Bass Boost 13 for music, movies and racing, 25 for action, the only way to add some sub-bass.

**Presets.** Bands in dB. Effects as the raw `.fac` value (0–127), with the value FxSound's interface shows in
brackets. Σ+ is the sum of the positive bands. Ambience is 0 in all of them.

| Preset | 62.5 | 116 | 214 | 397 | 735 | 1360 | 2520 | 4670 | 8640 | 16k | Σ+ | Clarity | Surround | Dyn. Boost | Bass Boost | Leveling | Why |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| General | 0 | −1 | −1 | 0 | +2 | 0 | −3 | +2 | +1 | −1 | 5 | 13 (1.0) | 0 | 0 | 0 | 0 | The base correction: fills the 700 Hz dip, lowers the 3 kHz peak, lifts the 4.7 kHz dip |
| Music | +1 | −1 | −1 | 0 | +2 | 0 | −3 | +2 | +1 | −1 | 6 | 0 | 0 | 0 | 13 (1.0) | 0 | Neutral to the measurement, with a touch of sub-bass; no compression |
| Video | −1 | −2 | −2 | 0 | +2 | +1 | −3 | +2 | 0 | −1 | 5 | 13 (1.0) | 0 | 0 | 0 | 1.5 | Mostly speech: less bass, a little 1.4 kHz for intelligibility without touching the 3 kHz peak |
| Live Stream | −2 | −3 | −2 | 0 | +2 | +1 | −2 | +2 | 0 | −1 | 5 | 13 (1.0) | 0 | 13 (1.0) | 0 | 2 | Boomy stream mics and uneven levels: more low cut, gentle compression, more leveling |
| Sports TV | −1 | −2 | −2 | 0 | +2 | +1 | −2 | +2 | 0 | −1 | 5 | 13 (1.0) | 13 (1.0) | 0 | 0 | 1.5 | Clear commentator, a little width for the crowd; leveling evens out ads and the match |
| Voice | −4 | −3 | −2 | 0 | +2 | +1 | −2 | +2 | 0 | −2 | 5 | 0 | 0 | 0 | 0 | 1.5 | Calls: only the voice band, no effects, 16 kHz lowered for codec hiss |
| Movie | +1 | 0 | −1 | 0 | +2 | +1 | −3 | +2 | 0 | −1 | 6 | 0 | 13 (1.0) | 0 | 13 (1.0) | 0 | Body and some sub-bass for effects, present dialogue, slight width, dynamics intact |
| Movie Night | −2 | −2 | −1 | 0 | +2 | +1 | −2 | +2 | 0 | −1 | 5 | 13 (1.0) | 0 | 25 (2.0) | 0 | 2 | Low volume: bass held back, dialogue forward, strong compression and leveling, no widening |
| FPS | −3 | −3 | −2 | −1 | +1 | +1 | −1 | +3 | +1 | −1 | 6 | 0 | 0 | 0 | 0 | 0 | Footsteps and reloads at 1–5 kHz: bass out of the way, 2.5 kHz cut softened, 4.7 kHz lifted; image intact |
| TPS | −1 | −2 | −1 | 0 | +2 | +1 | −2 | +3 | 0 | −1 | 6 | 0 | 0 | 0 | 0 | 0 | Like FPS with more body, since ambience and dialogue matter too |
| MOBA | 0 | −1 | −1 | 0 | +2 | +1 | −3 | +2 | 0 | −1 | 5 | 13 (1.0) | 0 | 0 | 0 | 0 | Many cues and voices at 1–5 kHz that must stand apart, without fatigue in long matches |
| Strategy | 0 | −1 | −1 | 0 | +2 | 0 | −3 | +1 | 0 | −2 | 3 | 0 | 0 | 0 | 0 | 0 | Long sessions: as relaxed as possible, soft treble |
| RPG | +1 | −1 | −1 | 0 | +2 | 0 | −3 | +2 | +1 | −1 | 6 | 13 (1.0) | 13 (1.0) | 0 | 0 | 0 | Immersion: neutral with some sub-bass and slight width for music and ambience |
| Racing | +1 | 0 | −1 | 0 | +2 | 0 | −3 | +2 | +1 | −1 | 6 | 0 | 0 | 0 | 13 (1.0) | 0 | Engine body (90–200 Hz) and clear tyre squeal at 4–8 kHz |
| Sports Game | 0 | −1 | −1 | 0 | +2 | +1 | −2 | +2 | +1 | −1 | 6 | 13 (1.0) | 13 (1.0) | 0 | 0 | 0 | Clear commentary and a slightly wide stadium |
| Action | +1 | 0 | −1 | 0 | +2 | 0 | −3 | +2 | +1 | −1 | 6 | 0 | 13 (1.0) | 0 | 25 (2.0) | 0 | Impact in explosions from Bass Boost rather than raising bands; slight width |
| Horror | −1 | −1 | −1 | 0 | +2 | +1 | −2 | +2 | +1 | −1 | 6 | 0 | 0 | 0 | 0 | 0 | Small details (steps, whispers) clear and dynamics intact so the scares work |

## Edifier R12U desktop speakers

**What they are.** A small powered 2.0 pair: USB-powered, analogue input, 60 mm full-range drivers in a ported
(bass-reflex) cabinet, about 2 W RMS per side. No independent measurement was found, so the correction is estimated
from the specifications and from how a speaker of this size behaves: little output below roughly 150–180 Hz, little
headroom, dominant mids, and bass and lower mids reinforced by a nearby wall and the desk.

**Consequences for the presets.**

- **Cut the deep bass instead of boosting it.** 62.5 Hz is more than an octave below what the speakers reproduce
  (−6 to −10 dB depending on use), and 116 Hz is below their limit too (−2 to −6 dB). With FxSound's band width,
  these cuts act like a gentle high-pass filter that frees headroom and removes port chuffing.
- **Put the sense of bass at 214 Hz**, the lowest band that really plays (+1 or +2 dB at most).
- **Bass Boost is always 0**: it is centred on 90 Hz, exactly where the speakers make no sound, so it would only add
  distortion. With 2 W, a strong Dynamic Boost drives the amplifier into clipping quickly, so it is only used for
  Movie Night.
- −1 dB at 397 and 735 Hz for the boxy desk/wall reinforcement and the dominant mids; +1/+2 dB at 4670 and 8640 Hz
  for the limited top end; at most +1 at 16 kHz to avoid bringing up hiss.
- A **moderate** stereo widener (13–25) opens up the narrow desktop image for music, films and open-world games; it
  stays off where locating sounds matters (FPS, TPS, MOBA, horror) and for voice and live streams.

Outside FxSound, the setup matters more than any band: keep the Windows volume around 70–80 % and use the speakers'
own volume knob (their input clips with a hot line output), turn off the sound card's and Windows' own enhancements
(loudness, bass boost, virtual surround), and if possible move the speakers a few centimetres from the wall and
angle them towards your head.

**Presets.** Same columns as above. Bass Boost and Ambience are 0 in all of them.

| Preset | 62.5 | 116 | 214 | 397 | 735 | 1360 | 2520 | 4670 | 8640 | 16k | Σ+ | Clarity | Surround | Dyn. Boost | Leveling | Why |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| General | −6 | −3 | +1 | −1 | −1 | 0 | 0 | +2 | +2 | +1 | 6 | 13 (1.0) | 13 (1.0) | 0 | 0 | The base correction: removes what cannot play, lowers boxy mids, opens the treble |
| Music | −6 | −2 | +2 | −1 | −1 | 0 | 0 | +1 | +2 | +1 | 6 | 13 (1.0) | 13 (1.0) | 0 | 0 | A little more body at 214 Hz and air on top, gentle widening; no compression |
| Video | −8 | −5 | −1 | −1 | −1 | −1 | +1 | +2 | +1 | 0 | 4 | 13 (1.0) | 0 | 0 | 1.5 | Mostly speech: more bass cut, dominant mids lowered, presence at 2.5–4.7 kHz |
| Live Stream | −8 | −5 | −2 | −1 | −1 | −1 | +1 | +2 | +1 | 0 | 4 | 13 (1.0) | 0 | 0 | 1.5 | Like Video, with more cut at 214 Hz for boomy stream mics |
| Sports TV | −8 | −4 | −1 | −1 | −1 | 0 | +1 | +2 | +1 | 0 | 4 | 13 (1.0) | 13 (1.0) | 0 | 1.5 | Clear commentator, a little width for the crowd; leveling evens out ads and the match |
| Voice | −10 | −6 | −2 | −1 | −1 | 0 | +2 | +2 | 0 | −2 | 4 | 0 | 0 | 0 | 1.5 | Calls: only the voice band, no exciter or widener, 16 kHz lowered for hiss and sibilance |
| Movie | −6 | −2 | +2 | −1 | −1 | 0 | +1 | +2 | +1 | 0 | 6 | 13 (1.0) | 25 (2.0) | 0 | 0.5 | Body at 214 Hz, present dialogue and width; light leveling keeps explosions from overloading the speakers |
| Movie Night | −10 | −5 | +1 | 0 | −1 | 0 | +2 | +2 | 0 | −1 | 5 | 13 (1.0) | 0 | 25 (2.0) | 2 | Low volume at night: bass out, dialogue forward, strong compression and leveling, no widening |
| FPS | −10 | −6 | −2 | −1 | 0 | 0 | +2 | +3 | +1 | 0 | 6 | 0 | 0 | 0 | 0 | Footsteps and reloads at 2–5 kHz: bass out so it neither masks nor distorts, image intact |
| TPS | −8 | −4 | 0 | −1 | 0 | 0 | +1 | +3 | +1 | 0 | 5 | 13 (1.0) | 0 | 0 | 0 | Like FPS with a little more body for ambience and dialogue |
| MOBA | −8 | −4 | 0 | −1 | 0 | +1 | +1 | +2 | +1 | 0 | 5 | 13 (1.0) | 0 | 0 | 0 | Many cues at 1–5 kHz that must stand apart; on-screen positions without widening |
| Strategy | −8 | −4 | 0 | −1 | −1 | +1 | +1 | +1 | 0 | 0 | 3 | 13 (1.0) | 13 (1.0) | 0 | 0 | Long sessions: balanced and easy on the ears, clear unit cues, a little width |
| RPG | −6 | −2 | +2 | −1 | −1 | 0 | 0 | +2 | +1 | +1 | 6 | 13 (1.0) | 25 (2.0) | 0 | 0 | Immersion: body for music and ambience, more width |
| Racing | −6 | −2 | +2 | 0 | −1 | 0 | 0 | +2 | +1 | 0 | 5 | 13 (1.0) | 13 (1.0) | 0 | 0 | Engines live at 150–400 Hz: body there without reaching for frequencies the speakers cannot play |
| Sports Game | −6 | −3 | +1 | −1 | −1 | 0 | +1 | +2 | +1 | 0 | 5 | 13 (1.0) | 25 (2.0) | 0 | 0 | Clear commentary and a wide stadium |
| Action | −6 | −2 | +2 | −1 | −1 | 0 | 0 | +2 | +1 | 0 | 5 | 13 (1.0) | 25 (2.0) | 0 | 0 | Impact at 214 Hz instead of 62.5 Hz, plus width; no Bass Boost, which would make the speakers rattle |
| Horror | −6 | −2 | +1 | −1 | −1 | 0 | +1 | +2 | +1 | +1 | 6 | 0 | 0 | 0 | 0 | Detail and position of small sounds; no exciter or widener, no leveling so the scares work |

## Headphones vs speakers

The same category gets very different values on the two devices. On the headset the bass is already there and only
the sub-bass needs help, so Bass Boost is used and the work is in the 700 Hz–5 kHz region. On the small speakers the
deep bass simply does not exist, so it is cut, the body moves up to 214 Hz, Bass Boost stays off, and a moderate
widener is welcome because the desktop image is narrow, whereas on headphones widening mostly displaces footsteps.

## The `.fac` preset file

FxSound stores each user preset as a text file in `%APPDATA%\FxSound\Presets\` (built-in presets live in the
`Factsoft\` folder next to `FxSound.exe`). The preset's name is the **third line** of the file, not the file name.
Abridged example:

```text
CLASS1 : Effect Type
9: Version
Headphones - Music
0: Double Params Flag
1: Total number of elements
0: Main 0
0: Main 1
0: Main 2
0: Main 3
0: Main 4
13: Main 5
...
1: Integer[0]
1: Integer[1]
1: Integer[2]
1: Integer[3]
1: Integer[4]
0: Integer[5]
2: Integer[6]
10: Number of EQ Bands
1: On/Off Flag
Band 1
   62.5: CF
   1: Boost/Cut
Band 2
   116: CF
   -1: Boost/Cut
...
```

| Line | Effect | What it does |
| --- | --- | --- |
| `Main 0` | Fidelity / Clarity | Harmonic exciter (brightness and presence) |
| `Main 1` | 3D Surround | Delay-based stereo widener, not HRTF |
| `Main 2` | (unused) | — |
| `Main 3` | Ambience | Room reverb |
| `Main 4` | Dynamic Boost | Maximiser/limiter |
| `Main 5` | Bass Boost | Fixed boost centred on 90 Hz (Q 2.5), up to about 15 dB |
| `Integer[0..4]` | On flags for Clarity, Surround, Ambience, Dynamic Boost, Bass Boost | FxSound re-applies each effect from its `Main` value on load, so in practice an effect is on when its value is not 0 |
| `Integer[5]` | Binaural headphone mode | Not exposed in the current interface |
| `Integer[6]` | Music mode | Always forced to 2 on load |
| `Band n` | EQ band | Centre frequency (`CF`) and gain (`Boost/Cut`) |

- **Effect scale**: the file stores 0–127, mapped linearly to 0.0–1.0 and shown ×10 in the interface, so
  interface value ≈ `Main` / 12.7 (13 ≈ 1.0, 25 ≈ 2.0, 38 ≈ 3.0). How many dB each step gives depends on FxSound's
  internal curves, so the dB figures above are approximate.
- **EQ**: 10 bands, ±12 dB, 1 dB steps in the interface; the centre frequencies are stored in the file.
- **Not in the file**: the band width (Q) and the master gain are global FxSound settings, and so is volume
  leveling. FxAuto keeps a leveling value per preset in its own config and sends it together with the preset.
- If FxSound has an auto-saved copy of a preset (`%APPDATA%\FxSound\AutoSave\`), it loads that copy instead of the
  edited file. That is one reason to tune presets in FxSound itself (FxAuto's **Edit in FxSound**) rather than by
  editing the files by hand.

## Caveats

- The G435 correction rests on a single measurement (RTINGS, via AutoEq). Ear pads and clamping force mostly move
  the bass.
- There is no measurement of the R12U; its correction is an estimate from the specifications and from how small
  ported speakers behave. Placement (distance to the wall, angle, listening distance) changes the result a lot.
- The shooter recommendations (2–5 kHz, less bass) come from manufacturer guides and blog posts, not studies.
- In every case: start with `General` and `Movie` at normal and at high volume, and adjust by ear.

## Sources

- FxSound source code: <https://github.com/fxsound2/fxsound-app> (DSP preset layout, effect indices, EQ limits).
- AutoEq results for the G435 (RTINGS, HMS II.3):
  <https://github.com/jaakkopasanen/AutoEq/tree/master/results/Rtings/HMS%20II.3%20over-ear/Logitech%20G435%20LIGHTSPEED>
- RTINGS review of the Logitech G435: <https://www.rtings.com/headphones/reviews/logitech/g435-lightspeed-wireless>
- RTINGS comparisons: <https://www.rtings.com/headphones/tools/compare/logitech-g335-logitech-g435-lightspeed-wireless/26491/28292>,
  <https://www.rtings.com/headphones/tools/compare/turtle-beach-stealth-500-logitech-g435-lightspeed-wireless/59008/28292>
- Tom's Guide review of the G435: <https://www.tomsguide.com/reviews/logitech-g435>
- Edifier R12U specifications and reviews: <https://farnell.com/datasheets/2311509.pdf>,
  <https://www.galaxus.de/en/s1/product/edifier-r12u-20-pc-speakers-13917654>,
  <https://www.ldlc.com/en/reviews/PB00228856.html>,
  <https://arvutitark.ee/arvutid-ja-lisad/heliseadmed/kolarid/edifier-r12u-20-4-w-1128814>,
  <https://elmir.ua/response/product-instruction.php?id=334246>, <https://hexus.net/qacv2a>
- EQ for games: <https://attackshark.com/blogs/knowledges/fps-footstep-frequencies-2khz-4khz-mastery>,
  <https://evezone.evetech.co.za/deep-dives/eq-settings-footsteps-dialogue>,
  <https://windowsreport.com/equalizer-settings-gaming/>
