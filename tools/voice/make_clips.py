"""Make the companion's voice clips with Kokoro, a local text-to-speech model (no account, runs offline on the CPU).

Write lines in a text file, one per clip, as `name: text` (blank lines and # comments are ignored):

    battle_cry: Stand and fight!
    timber: Timber! The tree falls, as all things must.

then, from tools/voice:

    uv run python make_clips.py lines.txt                 # every line -> mod/sounds/<name>.ogg
    uv run python make_clips.py lines.txt --only timber   # just one
    uv run python make_clips.py --audition "I will be back."   # one line in several voices (or one --voice at each accent strength)

Clip names that match a moment play automatically: battle_cry, victory, timber, door, death, level_up, greeting,
respawn (add a suffix for variants: battle_cry2, battle_cry_angry; battle_cry__troll is for trolls only). Any other
name is one the agent can choose to add to something it says. `{enemy}` lines are made once per `@enemies` entry.

--accent gives English an Austrian-German accent by rewriting the pronunciation before it's spoken: 1 = "v" for "w",
"z"/"s" for "th", "bek" for "back", hard word endings; 2 also "sht"/"shp", a tapped r and "ch" for "j"; 3 (default) is
thick: a German r, "-a" for "-er", pure vowels ("goh", "shtohn"), "d" for "th", "schw-", and glottal stops between words.

The "barbarian" sound: the voice is generated a little fast, then slowed down, which drops its pitch (--pitch 0.85 is
about two semitones deeper) without changing the pace; --grit adds a touch of rasp. The first run downloads the
model (about 330 MB) from Hugging Face.
"""

import argparse
import json
import re
import sys
from pathlib import Path

import numpy as np
import soundfile as sf
from scipy.signal import resample_poly

RATE = 24000  # Kokoro's sample rate
HERE = Path(__file__).resolve().parent
DEFAULT_OUT = HERE.parents[1] / "mod" / "sounds"

# Deep male voices, and two non-English voices that give English an accent.
AUDITION_VOICES = ["am_onyx", "am_fenrir", "am_adam", "am_michael", "bm_george", "bm_lewis", "im_nicola", "em_alex"]

# Austrian-German English: phoneme rewrites (misaki's IPA-ish phonemes, which Kokoro speaks directly).
VOICED_TO_UNVOICED = {"d": "t", "z": "s", "v": "f", "ɡ": "k", "b": "p", "ʤ": "ʧ", "ʒ": "ʃ"}
STRESS = "ˈˌ"


def austrian(phonemes: str, level: int) -> str:
    if level <= 0:
        return phonemes
    words = []
    for word in phonemes.split(" "):
        # Split off trailing punctuation, so word endings can be found.
        core, tail = word, ""
        while core and core[-1] in ".,!?;:—…\"”":
            core, tail = core[:-1], core[-1] + tail
        w = core
        if level >= 4:
            words.append(arnold(w) + tail)
            continue
        if level >= 3:
            w = re.sub(r"^([ˈˌ]?)ð", r"\1d", w)  # "this" -> "dis", before level 1 makes the rest of the "th"s "z"
        w = w.replace("w", "v").replace("ð", "z").replace("θ", "s")
        w = w.replace("æ", "ɛ").replace("a", "ɛ")  # TRAP vowel: "back" -> "bek" (A, the FACE diphthong, is untouched)
        w = w.replace("ɪ", "i").replace("ʊ", "u").replace("ɜː", "œː").replace("ɜ", "œ")
        if level >= 2:
            w = w.replace("ʌ", "a").replace("ʤ", "ʧ").replace("ɹ", "ɾ")
            for s, sh in (("st", "ʃt"), ("sp", "ʃp")):
                lead = len(w) - len(w.lstrip(STRESS))
                if w[lead:].startswith(s):
                    w = w[:lead] + sh + w[lead + len(s):]
            if w.endswith("ŋ"):
                w += "k"
        if level >= 3:
            w = thick(w)
        # Final devoicing: German ends words on hard consonants ("bad" -> "bat").
        if w and w[-1] in VOICED_TO_UNVOICED:
            w = w[:-1] + VOICED_TO_UNVOICED[w[-1]]
        words.append(w + tail)
    return " ".join(words)


def thick(w: str) -> str:
    """Level 3, thick Austrian: German r, the -er ending, pure vowels, "schw-", and a glottal stop before a word that
    starts with a vowel (the clipped, staccato delivery)."""
    w = w.replace("ɹ", "ʁ").replace("ɾ", "ʁ")
    plain = w.replace("ˈ", "").replace("ˌ", "")
    for s, sh in (("sv", "ʃv"), ("sl", "ʃl"), ("sm", "ʃm"), ("sn", "ʃn")):
        if plain.startswith(s):
            w = sh + w[len(s):] if w.startswith(s) else w[0] + sh + w[1 + len(s):]
    # Pure German vowels for the English glides: "go" -> "goː", "stone" -> "ʃtoːn", "day" -> "deː".
    w = w.replace("Q", "oː").replace("O", "oː").replace("A", "eː").replace("ɒ", "ɔ").replace("ɛː", "ɛɐ")
    if len(plain) > 2 and w[-1] in "əɚ":
        w = w[:-1] + "ɐ"  # "over" -> "oːvɐ", "water" -> "vɔːtɐ"
    if plain[:1] in "aeiouɐɑɔəɛɜʊʌæœIAOQW":
        w = "ʔ" + w
    return w


VOWELS = "aeiouɐɑɒɔəɛɜɪʊʌæœIAOQW"


def arnold(w: str) -> str:
    """Level 4, Arnold: built on what's distinctive in his English rather than generic German. "v" for "w"; "d" for
    every voiced "th" ("dis", "dat", "de") and "s" for the other; "bek", "kam", "eet"; pure vowels ("goh", "deh");
    a light tapped r (British pronunciation underneath drops the final ones: "choppa"); "-a" endings; "sht"/"shp"/
    "shl"/"shm"/"shn"/"shv"; "ch" for "j"; hard endings; and the stressed vowel drawn out ("baack"). No glottal stops
    and no throaty r: those made level 3 sound choppy and French."""
    plain = w.replace("ˈ", "").replace("ˌ", "")
    w = w.replace("w", "v").replace("ð", "d").replace("θ", "s")
    w = w.replace("æ", "ɛ").replace("a", "ɛ").replace("ʌ", "a")
    w = w.replace("ɪ", "i").replace("ʊ", "u").replace("ɜː", "œː").replace("ɜ", "œ")
    w = w.replace("ɹ", "ɾ").replace("ʤ", "ʧ")
    w = w.replace("Q", "oː").replace("O", "oː").replace("A", "eː").replace("ɒ", "ɔ").replace("ɛː", "ɛɐ")
    for sp, sh in (("st", "ʃt"), ("sp", "ʃp"), ("sl", "ʃl"), ("sm", "ʃm"), ("sn", "ʃn"), ("sv", "ʃv")):
        lead = len(w) - len(w.lstrip(STRESS))
        if w[lead:].startswith(sp):
            w = w[:lead] + sh + w[lead + len(sp):]
    if w.endswith("ŋ"):
        w += "k"
    if len(plain) > 2 and w and w[-1] in "əɚ":
        w = w[:-1] + "ɐ"
    # Draw out the stressed vowel (the slow, deliberate delivery): the first vowel after the main stress mark.
    i = w.find("ˈ")
    if i >= 0:
        for j in range(i + 1, len(w)):
            if w[j] in VOWELS:
                # Plain vowels only: the capitals are diphthongs ("now", "my") and keep their glide.
                if w[j] not in "IAOQW" and (j + 1 >= len(w) or w[j + 1] != "ː"):
                    w = w[:j + 1] + "ː" + w[j + 1:]
                break
    if w and w[-1] in VOICED_TO_UNVOICED:
        w = w[:-1] + VOICED_TO_UNVOICED[w[-1]]
    return w


def staccato(text: str) -> str:
    """His halting delivery: a short pause after every second word ("I'll be, back"), never inside a name."""
    out = []
    for sentence in re.split(r"(?<=[.!?])\s+", text.strip()):
        words = sentence.split()
        for k, word in enumerate(words):
            last = k == len(words) - 1
            pause = k % 2 == 1 and not last and word[-1:].isalpha() and not words[k + 1][:1].isupper()
            out.append(word + ("," if pause else ""))
    return " ".join(out)


def parse_lines(path: Path) -> list[tuple[str, str]]:
    enemies: list[tuple[str, str]] = []
    templates = []
    for n, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("@enemies"):
            for item in line[len("@enemies"):].split(","):
                key, _, said = item.strip().partition("=")
                enemies.append((key.strip().lower(), (said or key).strip()))
            continue
        if ":" not in line:
            sys.exit(f"{path.name}:{n}: expected `name: text`")
        name, text = (part.strip() for part in line.split(":", 1))
        if not re.fullmatch(r"[a-z0-9_]+", name.lower().replace("{enemy}", "x")):
            sys.exit(f"{path.name}:{n}: clip names use letters, digits and _ only ({name!r})")
        templates.append((n, name.lower(), text))
    clips = []
    for n, name, text in templates:
        if "{enemy}" in name or "{enemy}" in text.lower():
            if not enemies:
                sys.exit(f"{path.name}:{n}: an {{enemy}} line needs an @enemies list")
            for key, said in enemies:
                clips.append((name.replace("{enemy}", key), text.replace("{Enemy}", said).replace("{enemy}", said.lower())))
        else:
            clips.append((name, text))
    return clips


class Voice:
    def __init__(self) -> None:
        from kokoro import KPipeline  # slow import; only when actually speaking

        # English pronunciation, American or British (for the British voices, b*_), whatever the voice.
        self.pipelines = {lang: KPipeline(lang_code=lang, repo_id="hexgrad/Kokoro-82M") for lang in ("a", "b")}

    def speak(self, text: str, voice: str, pitch: float, speed: float, grit: float, accent: int = 0, halting: bool = False) -> np.ndarray:
        pipeline = self.pipelines["b" if voice.startswith("b") or accent >= 4 else "a"]
        if halting:
            text = staccato(text)
        phonemes, _ = pipeline.g2p(text)
        phonemes = austrian(phonemes, accent)
        # Generate faster by 1/pitch, then stretch it back out: slower playback lowers the pitch, the pace evens out.
        parts = [np.asarray(r.audio, dtype=np.float32) for r in pipeline.generate_from_tokens(phonemes, voice=voice, speed=speed / pitch)]
        audio = np.concatenate(parts) if parts else np.zeros(1, dtype=np.float32)
        up, down = _ratio(1.0 / pitch)
        if up != down:
            audio = resample_poly(audio, up, down).astype(np.float32)
        if grit > 0:
            audio = np.tanh(audio * (1.0 + 6.0 * grit)) / np.tanh(1.0 + 6.0 * grit)  # soft saturation
        return _finish(audio)


def _ratio(x: float) -> tuple[int, int]:
    """x as a fraction for resample_poly (to 1/100)."""
    return int(round(x * 100)), 100


def _finish(audio: np.ndarray) -> np.ndarray:
    """Trim silence, fade the ends, normalise to -1 dBFS."""
    loud = np.flatnonzero(np.abs(audio) > 0.01)
    if loud.size:
        audio = audio[max(0, loud[0] - 400): loud[-1] + 2400]
    fade = min(240, audio.size // 4)
    if fade:
        audio[:fade] *= np.linspace(0, 1, fade)
        audio[-fade:] *= np.linspace(1, 0, fade)
    peak = np.max(np.abs(audio)) or 1.0
    return audio * (0.89 / peak)


def write(path: Path, audio: np.ndarray) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.suffix == ".ogg":
        sf.write(path, audio, RATE, format="OGG", subtype="VORBIS")
    else:
        sf.write(path, audio, RATE)
    print(f"  {path.name}  {audio.size / RATE:.1f} s")


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("lines", nargs="?", type=Path, help="text file of `name: text` lines")
    ap.add_argument("--only", help="make just this clip")
    ap.add_argument("--voice", default="bm_george", help="Kokoro voice (default bm_george), or a blend: am_michael,bm_george")
    ap.add_argument("--accent", type=int, default=3, choices=[0, 1, 2, 3, 4], help="Austrian accent: 0 none, 1 light, 2 strong, 3 thick (default), 4 Arnold")
    ap.add_argument("--halting", action="store_true", help="a short pause after every second word")
    ap.add_argument("--arnold-auditions", action="store_true", help="the Arnold accent in a grid of voices, paces and pitches")
    ap.add_argument("--pitch", type=float, default=0.85, help="pitch factor, below 1 is deeper (default 0.85)")
    ap.add_argument("--speed", type=float, default=0.95, help="speaking pace (default 0.95)")
    ap.add_argument("--grit", type=float, default=0.3, help="rasp, 0-1 (default 0.3)")
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT, help="output folder (default mod/sounds)")
    ap.add_argument("--audition", metavar="TEXT", help="say TEXT in several voices into tools/voice/auditions/")
    args = ap.parse_args()
    args.voice_given = any(a == "--voice" or a.startswith("--voice=") for a in sys.argv[1:])

    voice = Voice()
    if args.arnold_auditions:
        text = args.audition or "I'll be back. Get to the boat, now! Come with me if you want to live."
        # (file name, voice, pitch, speed, grit, halting)
        grid = [
            ("a_george", "bm_george", 1.0, 0.9, 0.0, False),
            ("b_michael", "am_michael", 1.0, 0.9, 0.0, False),
            ("c_onyx", "am_onyx", 1.0, 0.9, 0.0, False),
            ("d_michael_george", "am_michael,bm_george", 1.0, 0.9, 0.0, False),
            ("e_onyx_michael", "am_onyx,am_michael", 1.0, 0.9, 0.0, False),
            ("f_george_halting", "bm_george", 1.0, 0.9, 0.0, True),
            ("g_michael_deeper_slow", "am_michael", 0.93, 0.82, 0.0, False),
            ("h_fenrir", "am_fenrir", 1.0, 0.9, 0.0, False),
            ("i_george_old_settings", "bm_george", 0.85, 0.95, 0.3, False),
        ]
        out = HERE / "auditions" / "arnold"
        print(f"Arnold auditions ({text!r}):")
        for name, v, pitch, speed, grit, halting in grid:
            write(out / f"{name}.wav", voice.speak(text, v, pitch, speed, grit, 4, halting))
        print(f"Listen in {out}. Then make the clips with the one you like, e.g.\n"
              "  uv run python make_clips.py lines.txt --accent 4 --voice am_michael,bm_george --pitch 1.0 --speed 0.9 --grit 0")
        return
    if args.audition:
        print("Auditions:")
        if args.voice_given:
            # One voice at each accent strength.
            for level in (0, 1, 2, 3):
                write(HERE / "auditions" / f"{args.voice}_accent{level}.wav",
                      voice.speak(args.audition, args.voice, args.pitch, args.speed, args.grit, level))
        else:
            for v in AUDITION_VOICES:
                write(HERE / "auditions" / f"{v}.wav", voice.speak(args.audition, v, args.pitch, args.speed, args.grit, args.accent))
        print(f"Listen in {HERE / 'auditions'}.")
        return
    if not args.lines:
        ap.error("give a lines file, or --audition TEXT")
    clips = parse_lines(args.lines)
    if args.only:
        clips = [c for c in clips if c[0] == args.only.lower()]
        if not clips:
            sys.exit(f"no clip called {args.only!r} in {args.lines.name}")
    print(f"{len(clips)} clip(s), voice {args.voice}, accent {args.accent}, pitch {args.pitch}, speed {args.speed}, grit {args.grit}:")
    for name, text in clips:
        write(args.out / f"{name}.ogg", voice.speak(text, args.voice, args.pitch, args.speed, args.grit, args.accent, args.halting))
    # Captions: what each clip says, shown in his speech bubble when a moment plays one.
    captions_file = args.out / "captions.json"
    captions = json.loads(captions_file.read_text(encoding="utf-8")) if captions_file.exists() else {}
    captions.update({name: text for name, text in clips})
    captions = {k: v for k, v in captions.items() if (args.out / f"{k}.ogg").exists()}
    captions_file.write_text(json.dumps(captions, indent=1, ensure_ascii=False, sort_keys=True), encoding="utf-8")
    print(f"Done: {args.out}. Rebuild the mod (or copy the files) to hear them in game.")


if __name__ == "__main__":
    main()
