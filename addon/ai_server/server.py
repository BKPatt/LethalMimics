"""
LethalMimics AI sidecar. Runs only on the host's machine, started by the mod (or by hand).

  * Speech recognition (Whisper via transformers) on every utterance players send.
  * Voice bank per player: keeps the cleanest, most "normal speaking voice" segments (screams, whispers,
    clipping and junk are filtered) and builds a Qwen3-TTS voice-clone prompt from the best ones, up to
    --max-ref-seconds. The prompt is rebuilt as better audio arrives, so clones improve during the game.
  * Small LLM (any Ollama or OpenAI-compatible endpoint) that writes lines in a player's style and replies
    to people talking to them, with learned nicknames/real names in context.
  * Optional persistence (--persist) so voices, nicknames and speech style carry over between games.

Stdlib HTTP + JSON. Audio is exchanged as base64 little-endian PCM16 mono.
"""

from __future__ import annotations

import argparse
import base64
import collections
import io
import json
import logging
import math
import os
import re
import sys
import threading
import time
import traceback
import urllib.error
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import numpy as np

log = logging.getLogger("lethalaudio")

ANALYSIS_SR = 16000
MAX_SEGMENTS_PER_PLAYER = 40
MAX_STYLE_LINES = 80
TRANSCRIPT_WINDOW_SECONDS = 180
WHISPER_JUNK = {"you", "thank you", "thanks for watching", "thank you for watching", "bye", "so", "uh", "um", "hmm"}


# --------------------------------------------------------------------------------------------- utils

def pcm16_b64_to_float(b64: str) -> np.ndarray:
    raw = base64.b64decode(b64)
    return np.frombuffer(raw, dtype="<i2").astype(np.float32) / 32768.0


def float_to_pcm16_b64(audio: np.ndarray) -> str:
    pcm = np.clip(audio, -1.0, 1.0)
    return base64.b64encode((pcm * 32767.0).astype("<i2").tobytes()).decode("ascii")


def resample(audio: np.ndarray, sr: int, target: int) -> np.ndarray:
    if sr == target or audio.size == 0:
        return audio.astype(np.float32)
    import librosa
    return librosa.resample(audio.astype(np.float32), orig_sr=sr, target_sr=target).astype(np.float32)


def safe_key(key: str) -> str:
    return re.sub(r"[^A-Za-z0-9_.-]", "_", key)[:80] or "unknown"


def name_tokens(username: str) -> set[str]:
    """Words a username is likely to be spoken as: 'xX_Brant_99' -> {'brant'}."""
    parts = re.split(r"[^A-Za-z]+", re.sub(r"([a-z])([A-Z])", r"\1 \2", username or ""))
    return {p.lower() for p in parts if len(p) >= 3 and p.lower() not in {"the", "and", "xxx", "ttv", "yt"}}


def words_in(text: str) -> list[str]:
    return re.findall(r"[a-z']+", text.lower())


# ------------------------------------------------------------------------------------ voice analysis

def analyze(audio16: np.ndarray, baseline_f0: float | None) -> dict:
    """Loudness, pitch and voicing features used to reject screams/whispers and score clone suitability."""
    import librosa

    frame, hop = 400, 160  # 25ms / 10ms
    if audio16.size < frame * 4:
        return {"ok": False, "reason": "too short"}

    rms = librosa.feature.rms(y=audio16, frame_length=frame, hop_length=hop)[0]
    db = 20 * np.log10(np.maximum(rms, 1e-6))
    active = db > -45
    active_frac = float(active.mean())
    if active.sum() < 20:
        return {"ok": False, "reason": "silent"}

    speech_db = float(np.mean(db[active]))
    noise_floor = float(np.percentile(db, 10))
    dynamic = float(np.percentile(db, 90) - noise_floor)
    clip_frac = float(np.mean(np.abs(audio16) > 0.98))
    flatness = float(np.mean(librosa.feature.spectral_flatness(y=audio16, n_fft=512, hop_length=hop)[0][active[: len(rms)]]))

    f0, voiced, _ = librosa.pyin(audio16, fmin=65, fmax=1000, sr=ANALYSIS_SR, frame_length=1024, hop_length=hop)
    n = min(len(voiced), len(active))
    voiced_active = voiced[:n] & active[:n]
    voiced_frac = float(voiced_active.sum() / max(1, active[:n].sum()))
    f0_med = float(np.nanmedian(f0[:n][voiced_active])) if voiced_active.sum() >= 5 else float("nan")

    m = dict(speech_db=speech_db, noise_floor=noise_floor, dynamic=dynamic, clip_frac=clip_frac,
             flatness=flatness, voiced_frac=voiced_frac, f0=f0_med, active_frac=active_frac)

    # --- rejection rules
    if voiced_frac < 0.22 or (speech_db < -36 and flatness > 0.25):
        return {**m, "ok": False, "reason": "whisper/unvoiced"}
    if math.isnan(f0_med):
        return {**m, "ok": False, "reason": "no pitch"}
    base = baseline_f0 or 160.0
    if clip_frac > 0.005 or f0_med > max(420.0, base * 1.7) or (speech_db > -9 and f0_med > base * 1.35):
        return {**m, "ok": False, "reason": "scream/shout"}
    if dynamic < 12:
        return {**m, "ok": False, "reason": "noisy"}

    # --- quality score in [0, 1]
    level = math.exp(-((speech_db + 20) ** 2) / (2 * 8 ** 2))
    voicing = min(1.0, voiced_frac / 0.55)
    snr = min(1.0, max(0.0, (dynamic - 12) / 25))
    typical = math.exp(-(math.log2(f0_med / base) ** 2) / (2 * 0.25 ** 2)) if baseline_f0 else 0.8
    score = 0.25 * level + 0.25 * voicing + 0.3 * snr + 0.2 * typical
    return {**m, "ok": True, "reason": "", "score": float(score)}


# ------------------------------------------------------------------------------------ player profile

class Segment:
    def __init__(self, seg_id: str, audio: np.ndarray, sr: int, text: str, score: float, f0: float, created: float):
        self.id, self.audio, self.sr, self.text = seg_id, audio, sr, text
        self.score, self.f0, self.created = score, f0, created

    @property
    def seconds(self) -> float:
        return self.audio.size / self.sr


class Profile:
    def __init__(self, key: str):
        self.key = key
        self.name = key
        self.aliases: collections.Counter[str] = collections.Counter()
        self.lines: collections.deque[str] = collections.deque(maxlen=MAX_STYLE_LINES)
        self.generated: collections.deque[str] = collections.deque(maxlen=12)
        self.segments: list[Segment] = []
        self.prompt = None
        self.prompt_sig: tuple = ()
        self.prompt_quality = 0.0
        self.prompt_seconds = 0.0
        self.last_rebuild = 0.0
        self.dirty = False
        self.lock = threading.RLock()

    @property
    def baseline_f0(self) -> float | None:
        f0s = [s.f0 for s in self.segments if not math.isnan(s.f0)]
        return float(np.median(f0s)) if len(f0s) >= 3 else None

    def confirmed_aliases(self) -> list[str]:
        return [a for a, c in self.aliases.most_common(6) if c >= 2]

    def all_names(self) -> set[str]:
        return name_tokens(self.name) | {a.lower() for a, c in self.aliases.items() if c >= 2}


# ------------------------------------------------------------------------------------------- server

class Brain:
    def __init__(self, args):
        self.args = args
        self.profiles: dict[str, Profile] = {}
        self.profiles_lock = threading.Lock()
        self.gpu_lock = threading.Lock()
        self.transcript: collections.deque[tuple[float, str, str, bool]] = collections.deque(maxlen=200)
        self.lines_since_alias_scan = 0
        self.tts = None
        self.asr = None
        self.state = "loading"
        self.error = ""
        self.llm_think_ok = True
        self.cache_dir = args.cache_dir

    # ---- model loading

    def load(self):
        try:
            import torch
            from transformers import pipeline

            device = self.args.device if torch.cuda.is_available() or not self.args.device.startswith("cuda") else "cpu"
            if device != self.args.device:
                log.warning("CUDA not available; running on CPU (this will be slow).")

            log.info("Loading ASR %s", self.args.asr_model)
            self.asr = pipeline("automatic-speech-recognition", model=self.args.asr_model,
                                dtype=torch.float16 if device.startswith("cuda") else torch.float32, device=device)

            log.info("Loading TTS %s", self.args.tts_model)
            attn = self.args.attn
            if attn == "auto":
                try:
                    import flash_attn  # noqa: F401
                    attn = "flash_attention_2"
                except ImportError:
                    attn = "sdpa"
            dtype = torch.bfloat16 if device.startswith("cuda") else torch.float32

            self.fast = False
            if device.startswith("cuda") and self.args.tts_backend in ("auto", "fast"):
                try:
                    # CUDA-graph inference: several times faster than the reference implementation.
                    from faster_qwen3_tts import FasterQwen3TTS
                    self.tts = FasterQwen3TTS.from_pretrained(self.args.tts_model, device=device, dtype=dtype, attn_implementation=attn)
                    self.tts.warmup(prefill_len=200)
                    self.fast = True
                except ImportError:
                    if self.args.tts_backend == "fast":
                        raise
            if not self.fast:
                from qwen_tts import Qwen3TTSModel
                self.tts = Qwen3TTSModel.from_pretrained(self.args.tts_model, device_map=device, dtype=dtype, attn_implementation=attn)
            self.state = "ready"
            log.info("Models ready (TTS backend: %s, attention: %s).", "faster-qwen3-tts" if self.fast else "qwen-tts", attn)
        except Exception as e:  # noqa: BLE001
            self.state = "error"
            self.error = f"{type(e).__name__}: {e}"
            log.error("Model load failed:\n%s", traceback.format_exc())

    # ---- profiles / persistence

    def profile(self, key: str, name: str | None = None) -> Profile:
        with self.profiles_lock:
            p = self.profiles.get(key)
            if p is None:
                p = Profile(key)
                self.profiles[key] = p
                if self.cache_dir:
                    self._load_profile(p)
        if name:
            p.name = name
        return p

    def _profile_dir(self, p: Profile) -> str:
        return os.path.join(self.cache_dir, safe_key(p.key))

    def _load_profile(self, p: Profile):
        import soundfile as sf
        d = self._profile_dir(p)
        meta_path = os.path.join(d, "profile.json")
        if not os.path.exists(meta_path):
            return
        try:
            with open(meta_path, encoding="utf-8") as f:
                meta = json.load(f)
            p.name = meta.get("name", p.name)
            p.aliases.update(meta.get("aliases", {}))
            p.lines.extend(meta.get("lines", []))
            for s in meta.get("segments", []):
                wav = os.path.join(d, f"{s['id']}.wav")
                if os.path.exists(wav):
                    audio, sr = sf.read(wav, dtype="float32")
                    p.segments.append(Segment(s["id"], audio, sr, s["text"], s["score"], s.get("f0", float("nan")), s.get("created", 0)))
            p.dirty = bool(p.segments)
            log.info("Loaded cached voice for %s: %d segments, %.1fs", p.name, len(p.segments), sum(s.seconds for s in p.segments))
        except Exception:  # noqa: BLE001
            log.warning("Could not load cached profile %s:\n%s", p.key, traceback.format_exc())

    def _save_profile(self, p: Profile):
        if not self.cache_dir:
            return
        import soundfile as sf
        d = self._profile_dir(p)
        os.makedirs(d, exist_ok=True)
        with p.lock:
            keep = {s.id for s in p.segments}
            for s in p.segments:
                wav = os.path.join(d, f"{s.id}.wav")
                if not os.path.exists(wav):
                    sf.write(wav, s.audio, s.sr, subtype="PCM_16")
            for fn in os.listdir(d):
                if fn.endswith(".wav") and fn[:-4] not in keep:
                    os.remove(os.path.join(d, fn))
            meta = dict(name=p.name, aliases=dict(p.aliases), lines=list(p.lines),
                        segments=[dict(id=s.id, text=s.text, score=s.score, f0=s.f0, created=s.created) for s in p.segments])
        tmp = os.path.join(d, "profile.json.tmp")
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(meta, f)
        os.replace(tmp, os.path.join(d, "profile.json"))

    # ---- ingest

    def ingest(self, req: dict) -> dict:
        key, name = req["key"], req.get("name") or req["key"]
        p = self.profile(key, name)
        sr = int(req["sample_rate"])
        audio = pcm16_b64_to_float(req["pcm16"])
        audio16 = resample(audio, sr, ANALYSIS_SR)
        source = req.get("source", "voice")

        text = self.transcribe(audio16)
        clean = re.sub(r"[^a-z' ]", "", text.lower()).strip()
        m = analyze(audio16, p.baseline_f0)
        # Whisper invents short phrases ("Okay.", "Thank you.") for noise; drop them unless the audio is clearly voiced.
        no_voice = not m.get("ok") and m.get("reason") in ("too short", "silent", "no pitch", "noisy", "whisper/unvoiced")
        if clean in WHISPER_JUNK or not clean or (no_voice and len(clean.split()) <= 2):
            return {"text": "", "accepted": False, "reason": "no speech", "addressed": [], **self._status(p)}

        now = time.time()
        p.lines.append(text)  # style examples (recorded clips count too)
        if source == "voice":  # only live speech is part of the conversation
            self.transcript.append((now, p.name, text, False))
            self.lines_since_alias_scan += 1

        seconds = audio16.size / ANALYSIS_SR
        words = len(words_in(text))
        accepted, reason = False, ""
        if not m.get("ok"):
            reason = m.get("reason", "rejected")
        elif seconds < 1.0:
            reason = "too short"
        elif not 0.8 <= words / seconds <= 5.5:
            reason = "transcript/length mismatch"
        else:
            score = m["score"] * (0.85 if source == "clip" else 1.0)
            seg = Segment(f"{int(now * 1000)}", audio, sr, text, score, m["f0"], now)
            with p.lock:
                p.segments.append(seg)
                if len(p.segments) > MAX_SEGMENTS_PER_PLAYER:
                    p.segments.sort(key=lambda s: s.score, reverse=True)
                    del p.segments[MAX_SEGMENTS_PER_PLAYER:]
                p.dirty = True
            accepted = True

        addressed = self.addressed_players(text, exclude=key)
        log.info("[%s] %r accepted=%s %s", p.name, text, accepted, reason)
        return {"text": text, "accepted": accepted, "reason": reason, "addressed": addressed, **self._status(p)}

    def transcribe(self, audio16: np.ndarray) -> str:
        kwargs = {"task": "transcribe"}
        if self.args.language.lower() != "auto":
            kwargs["language"] = self.args.language.lower()
        with self.gpu_lock:
            out = self.asr({"raw": audio16, "sampling_rate": ANALYSIS_SR}, generate_kwargs=kwargs)
        return (out.get("text") or "").strip()

    def addressed_players(self, text: str, exclude: str) -> list[str]:
        spoken = set(words_in(text))
        lowered = text.lower()
        hits = []
        for p in list(self.profiles.values()):
            if p.key == exclude:
                continue
            for n in p.all_names():
                if (" " in n and n in lowered) or n in spoken:
                    hits.append(p.key)
                    break
        return hits

    # ---- voice prompt (the part that "gets better")

    def select_reference(self, p: Profile) -> tuple[list[Segment], float, float]:
        base = p.baseline_f0
        def rank(s: Segment) -> float:
            typical = math.exp(-(math.log2(s.f0 / base) ** 2) / (2 * 0.25 ** 2)) if base and not math.isnan(s.f0) else 0.8
            return s.score * (0.6 + 0.4 * typical)

        chosen, total = [], 0.0
        for s in sorted(p.segments, key=rank, reverse=True):
            if total >= self.args.max_ref_seconds:
                break
            if total + s.seconds > self.args.max_ref_seconds * 1.15 and total >= self.args.min_ref_seconds:
                continue
            chosen.append(s)
            total += s.seconds
        quality = (sum(rank(s) for s in chosen) / len(chosen)) * min(1.0, total / self.args.max_ref_seconds) if chosen else 0.0
        return chosen, total, quality

    def maybe_rebuild(self, p: Profile, force: bool = False):
        if self.state != "ready" or not p.dirty:
            return
        if not force and time.time() - p.last_rebuild < 15:
            return
        with p.lock:
            chosen, total, quality = self.select_reference(p)
            p.dirty = False
            sig = tuple(s.id for s in chosen)
            if total < self.args.min_ref_seconds or sig == p.prompt_sig:
                return
            if p.prompt is not None and quality < p.prompt_quality + 0.02 and total <= p.prompt_seconds:
                return
            gap = np.zeros(int(0.25 * 24000), dtype=np.float32)
            parts = []
            for s in chosen:
                parts += [resample(s.audio, s.sr, 24000), gap]
            # Trailing silence keeps the model from running the reference's last word into the new line.
            ref_audio = np.concatenate(parts[:-1] + [np.zeros(12000, dtype=np.float32)])
            ref_text = " ".join(s.text for s in chosen)
        base_model = self.tts.model if self.fast else self.tts
        with self.gpu_lock:
            prompt = base_model.create_voice_clone_prompt(ref_audio=(ref_audio, 24000), ref_text=ref_text, x_vector_only_mode=False)
        with p.lock:
            p.prompt, p.prompt_sig, p.prompt_quality, p.prompt_seconds = prompt, sig, quality, total
            p.last_rebuild = time.time()
        log.info("Voice for %s rebuilt: %.1fs reference from %d segments, quality %.2f", p.name, total, len(chosen), quality)
        try:
            self._save_profile(p)
        except Exception:  # noqa: BLE001
            log.warning("Saving profile failed:\n%s", traceback.format_exc())

    def _status(self, p: Profile) -> dict:
        _, total, quality = self.select_reference(p)
        return {"key": p.key, "bank_seconds": round(sum(s.seconds for s in p.segments), 1),
                "ref_seconds": round(p.prompt_seconds, 1), "quality": round(p.prompt_quality, 3),
                "ready": p.prompt is not None, "aliases": p.confirmed_aliases()}

    # ---- speech generation

    def synthesize(self, p: Profile, text: str, out_sr: int) -> np.ndarray:
        if p.prompt is None:
            self.maybe_rebuild(p, force=True)
        if p.prompt is None:
            raise RuntimeError(f"No usable voice yet for {p.name}")
        lang = self.args.language if self.args.language.lower() != "auto" else "Auto"
        with self.gpu_lock:
            wavs, sr = self.tts.generate_voice_clone(text=text, language=lang, voice_clone_prompt=p.prompt,
                                                     max_new_tokens=int(12.5 * 12))  # cap at ~12s of speech
        wav = np.asarray(wavs[0], dtype=np.float32)
        peak = float(np.max(np.abs(wav))) if wav.size else 0.0
        if peak > 1e-4:
            wav *= min(4.0, 0.9 / peak)
        return resample(wav, sr, out_sr)

    # ---- LLM

    def llm(self, messages: list[dict], max_tokens: int = 60, temperature: float = 0.9, json_mode: bool = False) -> str:
        a = self.args
        if a.llm_api == "ollama":
            body = {"model": a.llm_model, "messages": messages, "stream": False, "keep_alive": "30m",
                    "options": {"temperature": temperature, "num_predict": max_tokens}}
            if json_mode:
                body["format"] = "json"
            if self.llm_think_ok:
                body["think"] = False
            try:
                data = self._post_json(a.llm_base.rstrip("/") + "/api/chat", body, a.llm_key)
            except urllib.error.HTTPError as e:
                if "think" in body and e.code == 400:
                    self.llm_think_ok = False
                    return self.llm(messages, max_tokens, temperature, json_mode)
                raise
            text = data["message"]["content"]
        else:
            base = a.llm_base.rstrip("/")
            url = base + ("/chat/completions" if base.endswith("/v1") else "/v1/chat/completions")
            body = {"model": a.llm_model, "messages": messages, "max_tokens": max_tokens, "temperature": temperature}
            if json_mode:
                body["response_format"] = {"type": "json_object"}
            data = self._post_json(url, body, a.llm_key)
            text = data["choices"][0]["message"]["content"]
        return re.sub(r"<think>.*?</think>", "", text or "", flags=re.S).strip()

    def ensure_llm(self):
        """For a local Ollama: start it if it isn't running and pull the model if it's missing."""
        a = self.args
        if a.llm_api != "ollama":
            return
        base = a.llm_base.rstrip("/")
        host = urllib.parse.urlparse(base).hostname or ""

        def tags():
            with urllib.request.urlopen(base + "/api/tags", timeout=3) as r:
                return [m.get("name", "") for m in json.loads(r.read()).get("models", [])]

        try:
            models = tags()
        except Exception:  # noqa: BLE001
            if host not in ("127.0.0.1", "localhost", "::1"):
                log.warning("Ollama at %s is not reachable.", base)
                return
            import shutil
            import subprocess
            exe = shutil.which("ollama") or os.path.join(os.environ.get("LOCALAPPDATA", ""), "Programs", "Ollama", "ollama.exe")
            if not os.path.exists(exe):
                log.warning("Ollama is not running and was not found. Install it from https://ollama.com (LLM lines/replies disabled).")
                return
            log.info("Starting Ollama (%s serve)", exe)
            flags = (subprocess.CREATE_NO_WINDOW | subprocess.DETACHED_PROCESS) if sys.platform == "win32" else 0
            subprocess.Popen([exe, "serve"], creationflags=flags, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            for _ in range(30):
                time.sleep(1)
                try:
                    models = tags()
                    break
                except Exception:  # noqa: BLE001
                    continue
            else:
                log.warning("Ollama did not start.")
                return

        wanted = a.llm_model if ":" in a.llm_model else a.llm_model + ":latest"
        if wanted not in models and a.llm_model not in models:
            log.info("Pulling LLM %s into Ollama (one-time download)...", a.llm_model)
            try:
                self._post_json(base + "/api/pull", {"model": a.llm_model, "stream": False}, timeout=3600)
                log.info("LLM %s ready.", a.llm_model)
            except Exception as e:  # noqa: BLE001
                log.warning("Could not pull %s: %s", a.llm_model, e)

    @staticmethod
    def _post_json(url: str, body: dict, key: str = "", timeout: float = 60) -> dict:
        req = urllib.request.Request(url, data=json.dumps(body).encode(), headers={"Content-Type": "application/json"})
        if key:
            req.add_header("Authorization", f"Bearer {key}")
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return json.loads(r.read())

    @staticmethod
    def clean_line(text: str, speaker_name: str) -> str:
        text = text.strip().split("\n")[0]
        text = re.sub(r"^\s*\[?[\w .'-]{1,24}\]?\s*:\s*", "", text)          # "Name: ..."
        text = re.sub(r"\*[^*]*\*|\([^)]*\)", "", text)                      # *actions* / (notes)
        text = text.strip().strip('"\u201c\u201d').strip()
        text = re.sub(r"[^\x20-\x7E]", "", text)                              # emoji etc.
        words = text.split()
        if len(words) > 20:
            text = " ".join(words[:20])
        return text.strip()

    def recent_chat(self, seconds: float = TRANSCRIPT_WINDOW_SECONDS, limit: int = 14) -> str:
        cutoff = time.time() - seconds
        rows = [f"{name}: {text}" for t, name, text, _ in self.transcript if t >= cutoff]
        return "\n".join(rows[-limit:]) or "(nothing yet)"

    def persona(self, p: Profile, situation: str) -> str:
        aliases = p.confirmed_aliases()
        style = list(p.lines)[-25:]
        style_block = "\n".join(f"- {line}" for line in style) if style else "- (no examples yet; sound like a casual gamer)"
        others = []
        for o in self.profiles.values():
            if o.key != p.key and o.confirmed_aliases():
                others.append(f"{o.name} is called {', '.join(o.confirmed_aliases())}")
        return (
            f"You voice {p.name} in a Lethal Company voice chat (a co-op horror game). {p.name} is actually a monster wearing "
            f"{p.name}'s voice, and wants the crewmate to come closer or stay put. Talk like {p.name} would: their word choice, "
            f"slang and energy, but NEVER repeat their old lines word for word. One spoken sentence, 4-14 words. "
            f"Output only {p.name}'s words: no quotes, no name prefix, no emojis, no actions. Never mention AI or monsters.\n"
            + (f"Friends call {p.name}: {', '.join(aliases)} (that's you; don't call yourself that).\n" if aliases else "")
            + (f"Names people use for others: {'; '.join(others)}. Call people by these names like a friend would.\n" if others else "")
            + f"Situation: {situation or 'unknown'}\n"
            + f"{p.name}'s real lines (style reference only):\n{style_block}"
        )

    def respond(self, req: dict) -> dict:
        p = self.profile(req["key"], req.get("name"))
        speaker = req.get("speaker_name", "someone")
        heard = req.get("heard", "")
        messages = [
            {"role": "system", "content": self.persona(p, req.get("situation", ""))},
            {"role": "user", "content": f"Recent chat:\n{self.recent_chat()}\n\n{speaker} says to {p.name}: \"{heard}\"\n"
                                        f"{p.name} answers it directly:"},
        ]
        t0 = time.time()
        text = self.clean_line(self.llm(messages, temperature=0.85), p.name)
        log.info("LLM reply in %.2fs", time.time() - t0)
        return self._speak_result(p, text, req)

    def line(self, req: dict) -> dict:
        p = self.profile(req["key"], req.get("name"))
        avoid = "; ".join(p.generated) or "none"
        messages = [
            {"role": "system", "content": self.persona(p, req.get("situation", ""))},
            {"role": "user", "content": f"Recent chat:\n{self.recent_chat()}\n\nSay one new short thing {p.name} might call out "
                                        f"to get a crewmate's attention or get them to come over. Don't reuse any of these: {avoid}\n"
                                        f"{p.name} says:"},
        ]
        text = self.clean_line(self.llm(messages, temperature=1.0), p.name)
        p.generated.append(text)
        return self._speak_result(p, text, req)

    def _speak_result(self, p: Profile, text: str, req: dict) -> dict:
        if not text:
            raise RuntimeError("LLM returned nothing usable")
        out_sr = int(req.get("sample_rate", 24000))
        t0 = time.time()
        audio = self.synthesize(p, text, out_sr)
        log.info("TTS %.1fs of audio in %.2fs", audio.size / out_sr, time.time() - t0)
        if req.get("log_as_said", True):
            self.transcript.append((time.time(), p.name, text, True))
        log.info("[%s] (mimic) %r", p.name, text)
        return {"text": text, "sample_rate": out_sr, "pcm16": float_to_pcm16_b64(audio)}

    # ---- nickname / real-name learning

    def scan_aliases(self):
        rows = [(name, text) for t, name, text, mimic in self.transcript if not mimic][-40:]
        if len(rows) < 4:
            return
        roster = [p.name for p in self.profiles.values()]
        chat = "\n".join(f"{n}: {t}" for n, t in rows)
        messages = [
            {"role": "system", "content": "You extract names from game voice chat transcripts. Answer with JSON only."},
            {"role": "user", "content":
                f"Players (usernames): {json.dumps(roster)}\nTranscript (speaker: words):\n{chat}\n\n"
                "Which spoken names or nicknames (real first names, short forms) do people use to talk to or about each player? "
                "Only include names that literally appear in the transcript words, and only when it is clear who they refer to "
                "(e.g. someone says a name and that player answers). Output a JSON object mapping each username to a list of names."},
        ]
        try:
            raw = self.llm(messages, max_tokens=200, temperature=0.1, json_mode=True)
            found = json.loads(raw[raw.find("{"): raw.rfind("}") + 1])
        except Exception:  # noqa: BLE001
            log.debug("Alias scan failed:\n%s", traceback.format_exc())
            return
        spoken = " ".join(t.lower() for _, t in rows)
        by_name = {p.name: p for p in self.profiles.values()}
        for username, names in (found or {}).items():
            p = by_name.get(username)
            if p is None or not isinstance(names, list):
                continue
            for n in names:
                n = str(n).strip()
                if 2 <= len(n) <= 20 and n.lower() in spoken and n.lower() not in {o.name.lower() for o in by_name.values() if o is not p}:
                    p.aliases[n] += 1
            if p.confirmed_aliases():
                log.info("Names for %s: %s", p.name, p.confirmed_aliases())

    # ---- background maintenance

    def maintenance_loop(self):
        last_save = time.time()
        while True:
            time.sleep(2.0)
            if self.state != "ready":
                continue
            try:
                for p in list(self.profiles.values()):
                    self.maybe_rebuild(p)
                if self.lines_since_alias_scan >= 10:
                    self.lines_since_alias_scan = 0
                    self.scan_aliases()
                if self.cache_dir and time.time() - last_save > 60:
                    last_save = time.time()
                    for p in list(self.profiles.values()):
                        self._save_profile(p)
            except Exception:  # noqa: BLE001
                log.warning("Maintenance error:\n%s", traceback.format_exc())

    def roster(self, req: dict) -> dict:
        for pl in req.get("players", []):
            self.profile(pl["key"], pl.get("name"))
        return self.status()

    def status(self) -> dict:
        return {"state": self.state, "error": self.error,
                "players": {k: self._status(p) for k, p in list(self.profiles.items())}}


# --------------------------------------------------------------------------------------------- HTTP

def make_handler(brain: Brain):
    routes = {
        "/ingest": brain.ingest,
        "/respond": brain.respond,
        "/line": brain.line,
        "/roster": brain.roster,
        "/speak": lambda r: brain._speak_result(brain.profile(r["key"], r.get("name")), r["text"], r),
    }

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, fmt, *args):  # quiet default access log
            log.debug(fmt, *args)

        def _send(self, code: int, obj: dict):
            data = json.dumps(obj).encode()
            self.send_response(code)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def do_GET(self):
            if self.path == "/health":
                self._send(200, brain.status())
            else:
                self._send(404, {"error": "not found"})

        def do_POST(self):
            fn = routes.get(self.path)
            if fn is None:
                return self._send(404, {"error": "not found"})
            if brain.state != "ready" and self.path != "/roster":
                return self._send(503, {"error": f"models {brain.state}", "detail": brain.error})
            try:
                body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))) or b"{}")
                self._send(200, fn(body))
            except Exception as e:  # noqa: BLE001
                log.warning("%s failed: %s", self.path, traceback.format_exc())
                self._send(500, {"error": f"{type(e).__name__}: {e}"})

    return Handler


def watch_parent(pid: int):
    """Exit when the game process exits so the models don't linger in VRAM."""
    if sys.platform == "win32":
        import ctypes
        SYNCHRONIZE = 0x00100000
        handle = ctypes.windll.kernel32.OpenProcess(SYNCHRONIZE, False, pid)
        if handle:
            ctypes.windll.kernel32.WaitForSingleObject(handle, 0xFFFFFFFF)
    else:
        while True:
            try:
                os.kill(pid, 0)
            except OSError:
                break
            time.sleep(2)
    log.info("Game exited; shutting down.")
    os._exit(0)


def _default_llm_key() -> str:
    """LLM API key from the environment or a per-user file, so it never sits in a mod profile or on the command line."""
    key = os.environ.get("LETHALMIMICS_LLM_API_KEY", "").strip()
    if key:
        return key
    path = os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "LethalMimics", "llm_api_key.txt")
    try:
        with open(path, encoding="utf-8") as f:
            return f.read().strip()
    except OSError:
        return ""


def main():
    ap = argparse.ArgumentParser(description="LethalMimics AI voice server (host only)")
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=5279)
    ap.add_argument("--tts-model", default="Qwen/Qwen3-TTS-12Hz-1.7B-Base", help="HF repo id or local path")
    ap.add_argument("--asr-model", default="openai/whisper-large-v3-turbo", help="HF repo id or local path")
    ap.add_argument("--device", default="cuda:0")
    ap.add_argument("--attn", default="auto", choices=["auto", "sdpa", "flash_attention_2", "eager"])
    ap.add_argument("--tts-backend", default="auto", choices=["auto", "fast", "reference"],
                    help="fast = faster-qwen3-tts CUDA graphs (NVIDIA only), reference = official qwen-tts")
    ap.add_argument("--llm-api", default="ollama", choices=["ollama", "openai"])
    ap.add_argument("--llm-base", default="http://127.0.0.1:11434")
    ap.add_argument("--llm-model", default="hf.co/openbmb/MiniCPM5-2B-GGUF:Q8_0")
    ap.add_argument("--llm-key", default=_default_llm_key(),
                    help="API key for the LLM endpoint. Default: LETHALMIMICS_LLM_API_KEY or %%LOCALAPPDATA%%/LethalMimics/llm_api_key.txt")
    ap.add_argument("--language", default="English", help="English, Chinese, ... or Auto")
    ap.add_argument("--max-ref-seconds", type=float, default=12.0)
    ap.add_argument("--min-ref-seconds", type=float, default=4.0)
    ap.add_argument("--cache-dir", default="", help="Enable cross-game voice cache in this folder (empty = off)")
    ap.add_argument("--hf-home", default="", help="Where to download models (sets HF_HOME)")
    ap.add_argument("--hf-token", default="", help="Hugging Face token, only needed for private/gated models")
    ap.add_argument("--parent-pid", type=int, default=0)
    ap.add_argument("--log-file", default="")
    args = ap.parse_args()

    handlers = [logging.StreamHandler(sys.stdout)]
    if args.log_file:
        handlers.append(logging.FileHandler(args.log_file, mode="w", encoding="utf-8"))
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s", handlers=handlers)
    if args.hf_home:
        os.environ["HF_HOME"] = args.hf_home
    # All default models are public. A stale saved login token makes the Hub reject even public downloads,
    # so don't send it unless one is given explicitly.
    if args.hf_token:
        os.environ["HF_TOKEN"] = args.hf_token
    else:
        os.environ.setdefault("HF_HUB_DISABLE_IMPLICIT_TOKEN", "1")
    os.environ.setdefault("HF_HUB_DISABLE_SYMLINKS_WARNING", "1")

    brain = Brain(args)
    if args.parent_pid:
        threading.Thread(target=watch_parent, args=(args.parent_pid,), daemon=True).start()
    threading.Thread(target=brain.load, daemon=True).start()
    threading.Thread(target=brain.ensure_llm, daemon=True).start()
    threading.Thread(target=brain.maintenance_loop, daemon=True).start()

    # On Windows SO_REUSEADDR lets a second server silently share the port; fail loudly instead.
    ThreadingHTTPServer.allow_reuse_address = sys.platform != "win32"
    server = ThreadingHTTPServer((args.host, args.port), make_handler(brain))
    log.info("LethalMimics AI server listening on http://%s:%d (voice cache: %s)", args.host, args.port, args.cache_dir or "off")
    server.serve_forever()


if __name__ == "__main__":
    main()
