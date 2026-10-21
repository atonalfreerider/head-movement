"""Quest 3 splat-dancer frame-rate bench (desk test over adb; Python 3 standard library only).

Starts the installed app with the test switches (docs/QUEST.md "Test switches without hands"), waits for the -hmview vantage
point, then reads the headset's own frame statistics for a window: the VrApi line of the app (FPS, stale frames, CPU/GPU
level and clock, App = the app's GPU ms per frame, GPU %), the viewer's HM-PERF and HM-SPLATS lines; optionally pulls the
-hmshots stereo stills (both eyes). One JSON line per run in <out>/results.jsonl, the logcat in <out>/<label>.log.

    python Tools/quest_splat_bench.py run <label> <film> <seek> <view> [--x "-hmsplatset quest_25k -hmsplatscale 0.5"] [--shots 1]
    python Tools/quest_splat_bench.py plan [--only recap|john_lula] [--shots 1]   # the candidate sweep (docs/SPLAT_DANCERS.md "Quest")
    python Tools/quest_splat_bench.py table                                       # results.jsonl as a markdown table

The headset must be tracking: one that lay still in a dark room shows "Finding position in room" and holds every launch
(logcat "VioInitialization: Skipping: Device is static") until it is picked up. Runs stop below --min-battery %.
"""
import argparse
import json
import os
import re
import statistics
import subprocess
import sys
import time

PKG = "com.headmovement.viewer"
SHOTDIR = f"/sdcard/Android/data/{PKG}/files/hmshots"
VRAPI = re.compile(r"FPS=(\d+)/(\d+).*?Stale=(\d+).*?CPU\d/GPU=(\d+)/(\d+),(\d+)/(\d+)MHz.*?Temp=([\d.]+)C.*?App=([\d.]+)ms"
                   r".*?CPU&GPU=([\d.]+)ms.*?GPU%=([\d.]+),CPU%=([\d.]+)")

# the sweep: (label suffix, extra switches); every candidate at the three vantage points of each lesson
CANDIDATES = [
    ("installed", "-hmsplatset full -hmsplatscale 1"),       # = the 2026-10-21 install (full 160k, package composite)
    ("off", "-hmsplats off"),                                # the mesh avatars (baseline)
    ("d25", "-hmsplatset quest_25k -hmsplatscale 1"),
    ("d15", "-hmsplatset quest_15k -hmsplatscale 1"),
    ("d25_s07", "-hmsplatset quest_25k -hmsplatscale 0.7"),
    ("d25_s05", "-hmsplatset quest_25k -hmsplatscale 0.5"),
    ("d15_s05", "-hmsplatset quest_15k -hmsplatscale 0.5"),
    ("d40_s05", "-hmsplatset quest_40k -hmsplatscale 0.5"),
    ("d25_s05_sort2", "-hmsplatset quest_25k -hmsplatscale 0.5 -hmsplatsort 2"),
]
FILMS = {"recap": 120, "john_lula": 40}
VIEWS = ("start", "front", "overhead")


def find_adb():
    import glob
    c = sorted(glob.glob(r"C:/Program Files/Unity/Hub/Editor/*/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"))
    return c[-1] if c else "adb"


class Adb:
    def __init__(self, serial):
        self.exe, self.serial = find_adb(), serial

    def __call__(self, *a, timeout=180):
        r = subprocess.run([self.exe, "-s", self.serial, *a], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout)
        return r.stdout

    def now(self):
        return float(self("shell", "date", "+%s.%N").strip()[:17])

    def battery(self):
        m = re.search(r"level: (\d+)", self("shell", "dumpsys battery"))
        return int(m.group(1)) if m else -1


def run(adb, label, film, seek, view, extra="", shots=0, window=15.0, settle=7.0, out="Builds/quest_bench", display="immersive"):
    os.makedirs(out, exist_ok=True)
    adb("shell", "am", "force-stop", PKG)
    time.sleep(1.5)
    adb("logcat", "-G", "16M")
    adb("logcat", "-c")
    adb("shell", f"rm -f {SHOTDIR}/*.png")
    adb("shell", "am", "broadcast", "-a", "com.oculus.vrpowermanager.prox_close")
    args = f"-hmplay {film} -hmseek {seek} -hmvr-display {display} -hmview {view}" + (f" -hmshots {shots}" if shots else "") + (f" {extra}" if extra else "")
    t_start = adb.now()
    adb("shell", "am", "start", "-n", f"{PKG}/com.unity3d.player.UnityPlayerActivity", "-e", "unity", f"'{args}'")
    t_view = pid = None
    static = False
    t0 = time.time()
    while time.time() - t0 < 150 and t_view is None:
        time.sleep(3)
        for ln in adb("logcat", "-d", "-v", "epoch").splitlines():
            m = re.match(r"\s*(\d+\.\d+)\s+(\d+)", ln)
            if not m or float(m.group(1)) < t_start:
                continue
            if "HM-PERF view '" in ln:
                t_view, pid = float(m.group(1)), m.group(2)
                break
            if "Device is static" in ln:
                static = True
    res = {"label": label, "film": film, "seek": seek, "view": view, "extra": extra, "args": args}
    if t_view is None:
        res["error"] = "no -hmview marker in 150 s" + (" (the headset is not tracking: 'Device is static' - pick it up)" if static else "")
        open(os.path.join(out, f"{label}.log"), "w", encoding="utf-8").write(adb("logcat", "-d", "-v", "epoch"))
        return res
    wait = (t_view + settle + window) - adb.now() + 1.5
    if shots:
        wait = max(wait, (t_view + 3.5 + 6 * (shots - 1) + 4) - adb.now())
    if wait > 0:
        time.sleep(wait)
    dump = adb("logcat", "-d", "-v", "epoch")
    open(os.path.join(out, f"{label}.log"), "w", encoding="utf-8").write(dump)
    rows, perf, splats = [], [], []
    for ln in dump.splitlines():
        m = re.match(r"\s*(\d+\.\d+)\s+(\d+)\s+(\d+)\s+(\w)\s+(.*)", ln)
        if not m:
            continue
        t, p, msg = float(m.group(1)), m.group(2), m.group(5)
        if "HM-SPLATS tier" in msg or "HM-SPLATS splat dancers" in msg or "FALLBACK" in msg:
            res.setdefault("tier", []).append(msg[msg.index("HM-"):])
        if not (t_view + settle <= t <= t_view + settle + window):
            continue
        if p == pid and "VrApi" in msg and "FPS=" in msg:
            v = VRAPI.search(msg)
            if v:
                rows.append(dict(fps=int(v.group(1)), stale=int(v.group(3)), cpu_lvl=int(v.group(4)), gpu_lvl=int(v.group(5)),
                                 gpu_mhz=int(v.group(7)), temp=float(v.group(8)), app_ms=float(v.group(9)), gpu_pct=float(v.group(11))))
        elif "HM-PERF lesson=" in msg:
            perf.append(msg[msg.index("HM-PERF"):])
        elif "HM-SPLATS status" in msg:
            splats.append(msg[msg.index("HM-SPLATS"):])
    if rows:
        med = lambda k: statistics.median(r[k] for r in rows)
        res.update(n=len(rows), fps_med=med("fps"), fps_min=min(r["fps"] for r in rows), stale_sum=sum(r["stale"] for r in rows),
                   app_gpu_ms_med=med("app_ms"), gpu_pct_med=med("gpu_pct"), cpu_gpu_lvl=sorted({(r["cpu_lvl"], r["gpu_lvl"]) for r in rows}),
                   gpu_mhz=sorted({r["gpu_mhz"] for r in rows}), temp_max=max(r["temp"] for r in rows))
    res["perf"] = perf[-2:]
    res["splats_status"] = splats[-1:]
    if shots:
        sd = os.path.join(out, label + "_shots")
        os.makedirs(sd, exist_ok=True)
        for f in adb("shell", f"ls {SHOTDIR}").split():
            if f.endswith(".png"):
                adb("pull", f"{SHOTDIR}/{f}", os.path.join(sd, f))
        res["shots"] = sorted(os.listdir(sd))
    res["battery"] = adb.battery()
    return res


def table(out):
    p = os.path.join(out, "results.jsonl")
    print("| run | switches | fps (VrApi median / min) | stale frames in window | app GPU ms | CPU/GPU level | GPU MHz |")
    print("|---|---|---|---|---|---|---|")
    for ln in open(p, encoding="utf-8"):
        r = json.loads(ln)
        if "error" in r:
            print(f"| {r['label']} | `{r.get('extra', '')}` | {r['error']} | | | | |")
            continue
        print(f"| {r['label']} | `{r.get('extra', '')}` | {r.get('fps_med')} / {r.get('fps_min')} | {r.get('stale_sum')} | {r.get('app_gpu_ms_med')} | "
              f"{r.get('cpu_gpu_lvl')} | {r.get('gpu_mhz')} |")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("cmd", choices=("run", "plan", "table"))
    ap.add_argument("rest", nargs="*")
    ap.add_argument("--x", default="")
    ap.add_argument("--shots", type=int, default=0)
    ap.add_argument("--window", type=float, default=15)
    ap.add_argument("--serial", default="2G0YC1ZF8F063B")
    ap.add_argument("--out", default="Builds/quest_bench")
    ap.add_argument("--only", default="")
    ap.add_argument("--min-battery", dest="min_battery", type=int, default=12)
    a = ap.parse_args()
    if a.cmd == "table":
        table(a.out)
        return 0
    adb = Adb(a.serial)
    jobs = []
    if a.cmd == "run":
        label, film, seek, view = a.rest
        jobs.append((label, film, seek, view, a.x))
    else:
        for film, seek in FILMS.items():
            if a.only and film != a.only:
                continue
            for suffix, extra in CANDIDATES:
                for view in VIEWS:
                    jobs.append((f"{film}_{suffix}_{view}", film, seek, view, extra))
    for label, film, seek, view, extra in jobs:
        b = adb.battery()
        if 0 <= b < a.min_battery:
            print(f"battery {b} %: stopping (charge the headset, then run again)")
            return 3
        r = run(adb, label, film, seek, view, extra, a.shots, a.window, out=a.out)
        with open(os.path.join(a.out, "results.jsonl"), "a", encoding="utf-8") as fh:
            fh.write(json.dumps(r) + "\n")
        print(json.dumps({k: r.get(k) for k in ("label", "error", "fps_med", "fps_min", "stale_sum", "app_gpu_ms_med", "cpu_gpu_lvl", "battery")}))
    adb("shell", "am", "force-stop", PKG)
    return 0


if __name__ == "__main__":
    sys.exit(main())
