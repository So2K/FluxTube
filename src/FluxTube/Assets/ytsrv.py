"""FluxTube stream resolver: one warm yt-dlp kept alive, so each video costs only its own requests.

argv: <cache dir> <deno executable or empty> <worker threads>
stdin:  one JSON per line {"id": "...", "url": "...", "format": "...", "cookies": "path|null", "proxy": "...", "mark": bool}
stdout: one JSON per line {"id", "ok", "video", "audio", "headers", "title", "duration", "video_avail", "audio_avail", "ms", "error"}
"""
import json
import os
import queue
import sys
import threading
import time

import yt_dlp

try:
    # yt-dlp can keep the pre-processed player script between videos but ships with that switched off;
    # with it on, the JavaScript challenge takes ~0.5 s per video instead of ~1.5 s
    from yt_dlp.extractor.youtube.jsc._builtin import ejs as _ejs
    for _c in vars(_ejs).values():
        if isinstance(_c, type) and hasattr(_c, "_ENABLE_PREPROCESSED_PLAYER_CACHE"):
            _c._ENABLE_PREPROCESSED_PLAYER_CACHE = True
except Exception:
    pass

out_lock = threading.Lock()
jobs = queue.Queue()
local = threading.local()
CACHE = (sys.argv[1] if len(sys.argv) > 1 else None) or None
DENO = sys.argv[2] if len(sys.argv) > 2 else ""
THREADS = int(sys.argv[3]) if len(sys.argv) > 3 and sys.argv[3].isdigit() else 2
# the watch page, its initial data and the client configs are ~1.5 MB and 2-3 round trips we do not need for stream links
FAST_SKIP = ["webpage", "initial_data", "configs"]


def emit(obj):
    line = json.dumps(obj, ensure_ascii=True)
    with out_lock:
        sys.stdout.write(line + "\n")
        sys.stdout.flush()


def ydl_for(cookies, proxy, fmt, mark, fast):
    try:
        cookie_stamp = os.stat(cookies).st_mtime_ns if cookies else None
    except OSError:
        cookie_stamp = None
    key = (cookies, cookie_stamp, proxy, fmt, mark, fast)
    cache = getattr(local, "ydls", None)
    if cache is None:
        cache = local.ydls = {}
    if key in cache:
        return cache[key]
    if len(cache) >= 8:
        cache.clear()
    opts = {
        "quiet": True,
        "no_warnings": True,
        "skip_download": True,
        "noplaylist": True,
        "format": fmt,
        "mark_watched": mark,
        "cachedir": CACHE,
        # Fast path skips HLS for low-latency VOD; full fallback keeps HLS enabled for LIVE streams & HLS-only videos!
        "extractor_args": {"youtube": {"skip": ["hls", "translated_subs"] if fast else ["translated_subs"], "player_skip": FAST_SKIP if fast else []}},
    }
    if cookies:
        opts["cookiefile"] = cookies
    if proxy:
        opts["proxy"] = proxy
    if DENO:
        opts["js_runtimes"] = {"deno": {"path": DENO}}
    ydl = yt_dlp.YoutubeDL(opts)
    cache[key] = ydl
    return ydl


def pick(info):
    req = info.get("requested_formats")
    if req:
        v = next((f for f in req if f.get("vcodec") not in (None, "none")), req[0])
        a = next((f for f in req if f.get("acodec") not in (None, "none") and f is not v), None)
        return v, a
    return info, None


def extract(job, fast, fmt_override=None, no_cookies=False):
    cookies = None if no_cookies else job.get("cookies")
    fmt = fmt_override or job.get("format") or "bv*[height<=?1080]+ba/b"
    ydl = ydl_for(cookies, job.get("proxy"), fmt, bool(job.get("mark")), fast)
    return ydl.extract_info(job["url"], download=False)


def work():
    while True:
        job = jobs.get()
        t0 = time.time()
        try:
            try:
                info = extract(job, True)
            except Exception as e:
                err_l = str(e).lower()
                # A full extraction cannot fix an account challenge; repeating it only delays the click.
                if "sign in to confirm" in err_l or "not a bot" in err_l:
                    raise
                try:
                    info = extract(job, False)  # full extraction (includes HLS for live / SABR-only videos!)
                except Exception as e2:
                    err2_l = str(e2).lower()
                    if "requested format is not available" in err2_l or "no video formats" in err2_l:
                        info = extract(job, False, fmt_override="bv*+ba/b")
                    elif job.get("cookies") and "private" not in err2_l and "members" not in err2_l and "age" not in err2_l:
                        # Retry once anonymously if stale/soft-flagged cookies caused the refusal
                        info = extract(job, False, fmt_override="bv*+ba/b", no_cookies=True)
                    else:
                        raise
            v, a = pick(info)
            emit({
                "id": job["id"], "ok": True,
                "video": v.get("url"), "audio": a.get("url") if a else None,
                "headers": v.get("http_headers") or info.get("http_headers") or {},
                "title": info.get("title"), "duration": info.get("duration"),
                "height": v.get("height"),
                "video_avail": v.get("available_at") or 0, "audio_avail": (a.get("available_at") or 0) if a else 0,
                "ms": int((time.time() - t0) * 1000),
            })
        except Exception as e:  # report and keep serving
            emit({"id": job.get("id"), "ok": False, "error": str(e)[-400:], "ms": int((time.time() - t0) * 1000)})


def main():
    for _ in range(THREADS):
        threading.Thread(target=work, daemon=True).start()
    emit({"id": "ready", "ok": True, "version": yt_dlp.version.__version__})
    for line in sys.stdin:
        line = line.strip()
        if line:
            try:
                jobs.put(json.loads(line))
            except ValueError:
                pass


if __name__ == "__main__":
    main()
