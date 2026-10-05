"""Make the companion's voice clips with Chatterbox: a local voice-cloning model that speaks any text in the voice of a
short reference recording (10-20 s of clear speech), accent and delivery included. Runs on the GPU if there is one.

From tools/voice/chatterbox:

    uv run python clone_clips.py --ref ../samples/alvar.wav --audition "I'll be back."   # a grid of settings
    uv run python clone_clips.py ../lines.txt --ref ../samples/alvar.wav                   # every clip
    uv run python clone_clips.py ../lines.txt --ref ../samples/alvar.wav --only timber     # just one

Lines files, clip names and output are the same as make_clips.py (Kokoro): mod/sounds/<name>.ogg plus captions.json.
Rebuild the mod afterwards.

--exaggeration (0.25-2, default 0.7) pushes the delivery towards dramatic; --cfg (0-1, default 0.4) lower is slower
and more deliberate; --temperature varies it from take to take. --pitch below 1 deepens the result a little and
--grit adds rasp, as in make_clips.py, but both are off by default: the reference voice should carry it.
--speed 0.9 slows it 10% at the same pitch. The first
run downloads the model (about 2 GB) from Hugging Face.

Use a voice you have the right to: your own (doing an impression is fine), a friend's with their agreement, a
public-domain recording, or a voice actor's. Not a real person's voice copied from their recordings.
"""

import argparse
import json
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import make_clips  # noqa: E402  (shared: lines parsing, trimming, writing)

DEFAULT_OUT = HERE.parents[2] / "mod" / "sounds"


class Cloner:
    def __init__(self, ref: Path | None) -> None:
        import torch
        from chatterbox.tts import ChatterboxTTS

        self.torch = torch
        device = "cuda" if torch.cuda.is_available() else "cpu"
        print(f"Loading Chatterbox on {device}...")
        self.model = ChatterboxTTS.from_pretrained(device=device)
        self.ref = str(ref) if ref else None

    def speak(self, text: str, exaggeration: float, cfg: float, temperature: float, pitch: float, grit: float,
              seed: int, speed: float = 1.0) -> np.ndarray:
        self.torch.manual_seed(seed)
        wav = self.model.generate(text, audio_prompt_path=self.ref, exaggeration=exaggeration, cfg_weight=cfg,
                                  temperature=temperature)
        audio = wav.squeeze(0).cpu().numpy().astype(np.float32)
        if self.model.sr != make_clips.RATE:
            from scipy.signal import resample_poly
            audio = resample_poly(audio, make_clips.RATE, self.model.sr).astype(np.float32)
        if pitch != 1.0:
            # Played slower, then time-stretched back is beyond scipy; a plain resample lowers pitch and slows it a
            # touch, which suits him.
            from scipy.signal import resample_poly
            up, down = make_clips._ratio(1.0 / pitch)
            audio = resample_poly(audio, up, down).astype(np.float32)
        if speed != 1.0:
            # Slower (below 1) or faster, keeping the pitch.
            import librosa
            audio = librosa.effects.time_stretch(audio, rate=speed).astype(np.float32)
        if grit > 0:
            audio = np.tanh(audio * (1.0 + 6.0 * grit)) / np.tanh(1.0 + 6.0 * grit)
        return make_clips._finish(audio)


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("lines", nargs="?", type=Path, help="text file of `name: text` lines (as for make_clips.py)")
    ap.add_argument("--ref", type=Path, help="the reference recording (wav/mp3, 10-20 s); without it, Chatterbox's own voice")
    ap.add_argument("--only", help="make just this clip")
    ap.add_argument("--exaggeration", type=float, default=0.7)
    ap.add_argument("--cfg", type=float, default=0.4)
    ap.add_argument("--temperature", type=float, default=0.8)
    ap.add_argument("--pitch", type=float, default=1.0)
    ap.add_argument("--grit", type=float, default=0.0)
    ap.add_argument("--speed", type=float, default=1.0, help="below 1 is slower, same pitch (0.9 = 10%% slower)")
    ap.add_argument("--seed", type=int, default=7, help="the same seed gives the same take")
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT)
    ap.add_argument("--audition", metavar="TEXT", help="say TEXT at a grid of exaggeration/cfg settings into auditions/")
    args = ap.parse_args()
    if args.ref and not args.ref.exists():
        sys.exit(f"no such file: {args.ref}")

    cloner = Cloner(args.ref)
    if args.audition:
        out = HERE.parent / "auditions" / "chatterbox"
        print(f"Auditions ({args.audition!r}):")
        for ex in (0.4, 0.7, 1.0, 1.4):
            for cfg in (0.3, 0.5):
                write_wav(out / f"ex{ex:.1f}_cfg{cfg:.1f}.wav",
                          cloner.speak(args.audition, ex, cfg, args.temperature, args.pitch, args.grit, args.seed, args.speed))
        print(f"Listen in {out}, then make the clips with the settings you like (--exaggeration, --cfg).")
        return
    if not args.lines:
        ap.error("give a lines file, or --audition TEXT")
    clips = make_clips.parse_lines(args.lines)
    if args.only:
        clips = [c for c in clips if c[0] == args.only.lower()]
        if not clips:
            sys.exit(f"no clip called {args.only!r} in {args.lines.name}")
    print(f"{len(clips)} clip(s), ref {args.ref}, exaggeration {args.exaggeration}, cfg {args.cfg}:")
    for name, text in clips:
        make_clips.write(args.out / f"{name}.ogg",
                         cloner.speak(text, args.exaggeration, args.cfg, args.temperature, args.pitch, args.grit, args.seed, args.speed))
    captions_file = args.out / "captions.json"
    captions = json.loads(captions_file.read_text(encoding="utf-8")) if captions_file.exists() else {}
    captions.update({name: text for name, text in clips})
    captions = {k: v for k, v in captions.items() if (args.out / f"{k}.ogg").exists()}
    captions_file.write_text(json.dumps(captions, indent=1, ensure_ascii=False, sort_keys=True), encoding="utf-8")
    print(f"Done: {args.out}. Rebuild the mod (or copy the files) to hear them in game.")


def write_wav(path: Path, audio: np.ndarray) -> None:
    make_clips.write(path, audio)


if __name__ == "__main__":
    main()
