"""Make the companion's voice clips with Kokoro, a local text-to-speech model (no account, runs offline on the CPU).

Write lines in a text file, one per clip, as `name: text` (blank lines and # comments are ignored):

    battle_cry: Crom! Stand and fight!
    timber: Timber! The tree falls, as all things must.

then, from tools/voice:

    uv run python make_clips.py lines.txt                 # every line -> mod/sounds/<name>.ogg
    uv run python make_clips.py lines.txt --only timber   # just one
    uv run python make_clips.py --audition "Crom! Give me strength!"   # one line in several voices, to pick from

Clip names that match a moment play automatically: battle_cry, timber, death, level_up, greeting, respawn (add a suffix
for variants: battle_cry2, battle_cry_angry). Any other name is one the agent can choose to add to something it says.

The "barbarian" sound: the voice is generated a little fast, then slowed down, which drops its pitch (--pitch 0.85 is
about two semitones deeper) without changing the pace; --grit adds a touch of rasp. The first run downloads the
model (about 330 MB) from Hugging Face.
"""

import argparse
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


def parse_lines(path: Path) -> list[tuple[str, str]]:
    clips = []
    for n, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if ":" not in line:
            sys.exit(f"{path.name}:{n}: expected `name: text`")
        name, text = (part.strip() for part in line.split(":", 1))
        if not re.fullmatch(r"[a-z0-9_]+", name.lower()):
            sys.exit(f"{path.name}:{n}: clip names use letters, digits and _ only ({name!r})")
        clips.append((name.lower(), text))
    return clips


class Voice:
    def __init__(self) -> None:
        from kokoro import KPipeline  # slow import; only when actually speaking

        self.pipeline = KPipeline(lang_code="a", repo_id="hexgrad/Kokoro-82M")  # English pronunciation, any voice

    def speak(self, text: str, voice: str, pitch: float, speed: float, grit: float) -> np.ndarray:
        # Generate faster by 1/pitch, then stretch it back out: slower playback lowers the pitch, the pace evens out.
        parts = [np.asarray(audio, dtype=np.float32) for _, _, audio in self.pipeline(text, voice=voice, speed=speed / pitch)]
        audio = np.concatenate(parts) if parts else np.zeros(1, dtype=np.float32)
        up, down = _ratio(1.0 / pitch)
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
    ap.add_argument("--voice", default="am_onyx", help="Kokoro voice (default am_onyx)")
    ap.add_argument("--pitch", type=float, default=0.85, help="pitch factor, below 1 is deeper (default 0.85)")
    ap.add_argument("--speed", type=float, default=0.95, help="speaking pace (default 0.95)")
    ap.add_argument("--grit", type=float, default=0.3, help="rasp, 0-1 (default 0.3)")
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT, help="output folder (default mod/sounds)")
    ap.add_argument("--audition", metavar="TEXT", help="say TEXT in several voices into tools/voice/auditions/")
    args = ap.parse_args()

    voice = Voice()
    if args.audition:
        print("Auditions:")
        for v in AUDITION_VOICES:
            write(HERE / "auditions" / f"{v}.wav", voice.speak(args.audition, v, args.pitch, args.speed, args.grit))
        print(f"Listen in {HERE / 'auditions'}, then pick one with --voice.")
        return
    if not args.lines:
        ap.error("give a lines file, or --audition TEXT")
    clips = parse_lines(args.lines)
    if args.only:
        clips = [c for c in clips if c[0] == args.only.lower()]
        if not clips:
            sys.exit(f"no clip called {args.only!r} in {args.lines.name}")
    print(f"{len(clips)} clip(s), voice {args.voice}, pitch {args.pitch}, speed {args.speed}, grit {args.grit}:")
    for name, text in clips:
        write(args.out / f"{name}.ogg", voice.speak(text, args.voice, args.pitch, args.speed, args.grit))
    print(f"Done: {args.out}. Rebuild the mod (or copy the files) to hear them in game.")


if __name__ == "__main__":
    main()
