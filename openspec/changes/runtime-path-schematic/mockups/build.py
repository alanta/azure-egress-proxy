#!/usr/bin/env python3
"""Generates the Runtime-surface schematic mockup.

Everything visual is inherited from src/Portal/wwwroot/css/portal.css, which is embedded
verbatim; only the .sx-* rules below are new. Charts are rendered with the same maths
ChartModel uses (zero floor, series max, Height-6 with a 3px inset) so the shapes here are
the shapes the server would emit.
"""

import math
import random
import re
from pathlib import Path

HERE = Path(__file__).parent
REPO = Path("/home/marnix/Projects/Zure/azure-egress-proxy")
PORTAL_CSS = (REPO / "src/Portal/wwwroot/css/portal.css").read_text()

# ---------------------------------------------------------------- Azure icons

PIP_STOPS = ('<stop offset="0" stop-color="#32bedd"/><stop offset=".18" stop-color="#32caea"/>'
             '<stop offset=".41" stop-color="#32d2f2"/><stop offset=".78" stop-color="#32d4f5"/>')


def icon(name, key):
    """One official Azure architecture icon, with its gradient ids namespaced so several can
    share a page without the last one winning."""
    svg = (HERE / f"{name}.svg").read_text().strip()

    # The prefix icon ships two gradients that reference neither stops nor a base gradient;
    # left alone they render as nothing. Give them what gradient "a" has.
    for gid, x1, y1, x2, y2 in (("b", "9", "-1404.702", "9", "-1398.732"),
                                ("c", "12.945", "-1407.632", "12.945", "-1401.662")):
        broken = f'<linearGradient id="{gid}" x1="{x1}" y1="{y1}" x2="{x2}" y2="{y2}"/>'
        fixed = (f'<linearGradient id="{gid}" x1="{x1}" y1="{y1}" x2="{x2}" y2="{y2}" '
                 f'gradientTransform="matrix(1 0 0 -1 0 -1391.642)" gradientUnits="userSpaceOnUse">'
                 f'{PIP_STOPS}</linearGradient>')
        svg = svg.replace(broken, fixed)

    svg = re.sub(r'id="([a-z])"', rf'id="{key}-\1"', svg)
    svg = re.sub(r'url\(#([a-z])\)', rf'url(#{key}-\1)', svg)
    return svg


GLOBE = ('<svg viewBox="0 0 18 18" fill="none" stroke="#8b93a8" stroke-width="1.05">'
         '<circle cx="9" cy="9" r="7"/><ellipse cx="9" cy="9" rx="3" ry="7"/>'
         '<path d="M2.3 6.8h13.4M2.3 11.2h13.4"/></svg>')


# --------------------------------------------------------------------- charts

def series(base, jitter, n=60, spikes=(), zeros=(), ramp=0.0, seed=7):
    rng = random.Random(seed)
    out = []
    for i in range(n):
        v = base + ramp * (i / (n - 1)) + rng.uniform(-jitter, jitter)
        out.append(max(0.0, v))
    for i, v in spikes:
        out[i] = v
    for i in zeros:
        out[i] = 0.0
    return out


def chart(values, colour, width=320, height=46, baseline=False, dot=False, label=None, fill=True):
    """The same geometry as Portal.Components.ChartModel."""
    if len(values) < 2:
        return '<div class="note dim">No samples in this window.</div>'
    top = max(max(values), 1e-9)
    step = width / (len(values) - 1)

    def y(v):
        return height - (v / top * (height - 6)) - 3

    pts = "".join(f"{'M' if i == 0 else 'L'}{i * step:.2f},{y(v):.2f}" for i, v in enumerate(values))
    area = f"{pts}L{width},{height}L0,{height}Z"
    aria = f' role="img" aria-label="{label}"' if label else ' aria-hidden="true"'
    base = (f'<line x1="0" y1="{height - 3}" x2="{width}" y2="{height - 3}" stroke="currentColor" '
            f'stroke-opacity=".12" stroke-width="1"/>') if baseline else ""
    end = (f'<circle cx="{width}" cy="{y(values[-1]):.2f}" r="2.6" fill="{colour}"/>') if dot else ""
    body = f'<path d="{area}" fill="{colour}" fill-opacity=".14"/>' if fill else ""
    return (f'<svg viewBox="0 0 {width} {height}" width="100%" height="{height}" '
            f'preserveAspectRatio="none"{aria}>{base}{body}'
            f'<path d="{pts}" fill="none" stroke="{colour}" stroke-width="1.5" stroke-linejoin="round"/>'
            f'{end}</svg>')


ACCENT, VIOLET, GREEN, AMBER, RED = "#5b8def", "#6a6bd6", "#2f9e6b", "#d98324", "#d1495b"


# ---------------------------------------------------------------- components

def pill(text, variant):
    return f'<span class="pill {variant}">{text}</span>'


def fresh(text="Just now"):
    return f'<div class="freshness"><span class="dot"></span> {text}</div>'


def station(key, ico, tag, name, value, unit, lamp, sub=None, blind=False):
    cls = "sx-station a-" + key + (" blind" if blind else "")
    body = (f'<div class="sx-blindnote">{sub}</div>' if blind else
            f'<div class="sx-read"><span class="n">{value}</span><span class="u">{unit}</span></div>'
            + (f'<div class="sx-sub">{sub}</div>' if sub else ""))
    return (f'<div class="{cls}">'
            f'<span class="sx-lamp {lamp}"></span>'
            f'<div class="sx-top"><span class="azicon">{ico}</span>'
            f'<span class="sx-id"><span class="sx-tag">{tag}</span>'
            f'<span class="sx-name">{name}</span></span></div>'
            f'{body}</div>')


def pipe(col, chip, tone=""):
    return (f'<div class="sx-pipe {tone}" style="grid-column:{col}">'
            f'<span class="sx-duct"></span>'
            f'<span class="sx-chip">{chip}</span></div>')


def cap(col, ico, label):
    return (f'<div class="sx-cap" style="grid-column:{col}">'
            f'<span class="azicon sm">{ico}</span><span class="lbl">{label}</span></div>')


def readout(label, p, detail, values, colour):
    # Line on a track, not an area. These series sit at 100 most of the time, and a filled chart
    # of a pinned-at-max value reads as a progress bar rather than as a history.
    return (f'<div class="sx-ro"><div class="hd"><span class="l">{label}</span>{p}</div>'
            f'<div class="sx-track">{chart(values, colour, width=260, height=24, fill=False)}</div>'
            f'<div class="dt">{detail}</div></div>')


def node(name, image, ip, health, lamp="ok", flag=None):
    # The instance-level public IP is the address a partner actually sees, so it gets its own full
    # width rather than competing with the health pill for the end of a single line.
    return (f'<div class="sx-node">'
            f'<div class="hd"><span class="sx-lamp sm {lamp}"></span>'
            f'<span class="nm">{name}</span>'
            f'{(pill(*flag) + " ") if flag else ""}{health}</div>'
            f'<div class="sub">{image} · {ip}</div></div>')


def gauge(label, value, unit, detail, values, colour, tone=""):
    return (f'<div class="sx-gauge"><div class="eyebrow">{label}</div>'
            f'<div class="n {tone}">{value}<small>{unit}</small></div>'
            f'<div class="sx-mini">{chart(values, colour, width=200, height=34, baseline=True)}</div>'
            f'<div class="dt">{detail}</div></div>')


def slots(labels, used):
    chips = "".join(f'<div class="ip{" used" if i < used else ""}">{l}</div>'
                    for i, l in enumerate(labels))
    return f'<div class="pool">{chips}</div>'


# --------------------------------------------------------------------- cards

def card(title, eyebrow, freshness, stations, pipes, caps, lanes, consequence, flow):
    return f"""
<section class="card sx-card">
  <div class="sx-head">
    <div><div class="eyebrow">{eyebrow}</div><div class="sx-title">{title}</div></div>
    {freshness}
  </div>
  <div class="sx">
    {caps[0]}{pipes[0]}{stations[0]}{pipes[1]}{stations[1]}{pipes[2]}{stations[2]}{pipes[3]}{caps[1]}
    <div class="sx-rule"></div>
    <div class="sx-riser" style="grid-column:3"></div>
    <div class="sx-riser" style="grid-column:5"></div>
    <div class="sx-riser" style="grid-column:7"></div>
    {lanes[0]}{lanes[1]}{lanes[2]}
    {consequence}
    {flow}
  </div>
</section>"""


def consequence(icon_, text, tone=""):
    """The one sentence that belongs to the path rather than to any single stage — which is what
    makes it span the deck instead of living in the prefix lane."""
    return (f'<div class="sx-conseq {tone}"><span class="bico">{icon_}</span>'
            f'<span>{text}</span></div>')


def flowstrip(value, unit, detail, values, colour=ACCENT, tone=""):
    return (f'<div class="sx-flow"><div class="sx-flow-head">'
            f'<span class="eyebrow">Throughput leaving the prefix</span>'
            f'<span class="sx-flow-n {tone}">{value}<small>{unit}</small></span>'
            f'<span class="sx-flow-d">{detail}</span></div>'
            f'{chart(values, colour, width=1000, height=58, baseline=True, dot=False, label="Network out, 1-minute samples, last hour")}'
            f'</div>')


LB_ICON = icon("Load-Balancers", "lb")
VMSS_ICON = icon("VM-Scale-Sets", "vm")
PIP_ICON = icon("Public-IP-Prefixes", "pp")
VNET_ICON = icon("Virtual-Networks", "vn")

CAPS = (cap(1, VNET_ICON, "Workloads<br>spoke vnet"), cap(9, GLOBE, "Partner<br>endpoints"))


# ------------------------------------------------------- state 1: nominal

net1 = series(0.19, 0.035, spikes=[(24, 0.82)], seed=3)
cpu1 = series(0.34, 0.22, spikes=[(24, 1.0)], seed=11)
avail1 = series(1.0, 0.0, zeros=(7, 19, 20, 27), seed=5)
lb1 = series(100.0, 0.0, seed=1)

nominal = card(
    "Both nodes healthy — and every address in the prefix already assigned",
    "Egress path", fresh("Fleet just now · monitor just now"),
    stations=[
        station("lb", LB_ICON, "Load balancer", "egproxy-ilb", "100", "% data path", "ok",
                "All probes passing"),
        station("vmss", VMSS_ICON, "Scale set", "egproxy-vmss", "2", "of 2 instances online", "ok",
                "azure-linux-3-arm64 · both healthy"),
        station("pip", PIP_ICON, "IP prefix", "egproxy-egress", "2", "of 2 addresses in use", "bad",
                "0 spare · the fleet cannot add a node"),
    ],
    pipes=[pipe(2, ":4750"), pipe(4, "POOL"), pipe(6, "FULL", "warn"), pipe(8, "/31")],
    caps=CAPS,
    lanes=[
        f"""<div class="sx-lane a-lb"><div class="sx-lane-tag">Load balancer · Azure Monitor</div>
        {readout("Data path", pill("Available", "enforce"), "100% on the last 1-minute sample", lb1, GREEN)}
        {readout("Health probes", pill("All passing", "enforce"), "100% on the last 1-minute sample", lb1, GREEN)}
        {fresh("Just now")}</div>""",

        f"""<div class="sx-lane a-vmss"><div class="sx-lane-tag">Nodes · ARM instance view</div>
        <div class="sx-nodes">
        {node("egproxy000000", "azure-linux-3-arm64", "4.166.55.209", pill("Healthy", "enforce"))}
        {node("egproxy000001", "azure-linux-3-arm64", "4.166.55.208", pill("Healthy", "enforce"))}
        </div>
        <div class="sx-gauges">
        {gauge("CPU", "0", "%", "avg 0 · peak 1", cpu1, VIOLET)}
        {gauge("Availability", "100", "%", "100.0% of the last hour's samples reported available", avail1, GREEN, "good")}
        </div>
        {fresh("Just now")}</div>""",

        f"""<div class="sx-lane a-pip"><div class="sx-lane-tag">Egress prefix · ARM</div>
        <div class="sx-stats"><div class="stat"><div class="k">2<small>/2</small></div>
        <div class="l">addresses assigned</div></div>
        <div class="stat"><div class="k bad">0</div><div class="l">spare · nodes the fleet can still add</div></div></div>
        {slots([".208", ".209"], 2)}
        <div class="legend"><span><i style="background:rgba(47,158,107,.35)"></i> In use</span>
        <span><i style="background:rgba(255,255,255,.7);border:1px solid var(--line)"></i> Spare</span>
        <span class="mono">4.166.55.208/31</span></div>
        {fresh("Just now")}</div>""",
    ],
    consequence=consequence("⛔", "<strong>The prefix is the fleet's ceiling.</strong> Every address "
                            "is assigned, so a third node has none left to take — it would egress "
                            "from an address outside this block, one no partner has allowlisted, and "
                            "its traffic would be refused at the partner's edge rather than here.",
                            "bad"),
    flow=flowstrip("0.2", " MB/min", "avg 0.2 · peak 0.8 · 1-minute samples, last hour", net1),
)


# ------------------------------------------------ state 2: pressed / degraded

net2 = series(3.4, 0.9, spikes=[(41, 6.2), (42, 5.7)], ramp=1.6, seed=23)
cpu2 = series(46.0, 9.0, ramp=34.0, seed=29)
avail2 = series(1.0, 0.0, zeros=tuple(range(38, 47)), seed=5)
probes2 = series(100.0, 0.0, seed=1)
for i in range(40, 60):
    probes2[i] = 66.0

degraded = card(
    "A node is out of the pool, and the prefix has room to replace it",
    "Egress path", fresh("Fleet just now · load balancer ~2 min ago · monitor just now"),
    stations=[
        station("lb", LB_ICON, "Load balancer", "egproxy-ilb", "100", "% data path", "warn",
                "1 of 3 probes failing"),
        station("vmss", VMSS_ICON, "Scale set", "egproxy-vmss", "2", "of 3 instances online", "bad",
                "1 unhealthy · 1 provisioning"),
        station("pip", PIP_ICON, "IP prefix", "egproxy-egress", "6", "of 8 addresses in use", "ok",
                "2 spare · room for the replacement"),
    ],
    pipes=[pipe(2, ":4750"), pipe(4, "2 OF 3", "warn"), pipe(6, "SNAT"), pipe(8, "/29")],
    caps=CAPS,
    lanes=[
        f"""<div class="sx-lane a-lb"><div class="sx-lane-tag">Load balancer · Azure Monitor</div>
        {readout("Data path", pill("Available", "enforce"), "100% on the last 1-minute sample", lb1, GREEN)}
        {readout("Health probes", pill("Some failing", "report"), "66% on the last 1-minute sample", probes2, AMBER)}
        {fresh("~2 min ago")}</div>""",

        f"""<div class="sx-lane a-vmss"><div class="sx-lane-tag">Nodes · ARM instance view</div>
        <div class="sx-nodes">
        {node("egproxy000000", "azure-linux-3-arm64", "4.166.55.209", pill("Healthy", "enforce"))}
        {node("egproxy000003", "azure-linux-3-arm64", "4.166.55.212", pill("Unhealthy", "open"), lamp="bad")}
        {node("egproxy000004", "azure-linux-3-arm64", "no address readable", pill("No health probe", "plain"), lamp="off", flag=("Updating", "report"))}
        </div>
        <div class="sx-gauges">
        {gauge("CPU", "81", "%", "avg 63 · peak 88", cpu2, VIOLET, "warn")}
        {gauge("Availability", "85", "%", "85.0% of the last hour's samples reported available", avail2, AMBER, "warn")}
        </div>
        {fresh("Just now")}</div>""",

        f"""<div class="sx-lane a-pip"><div class="sx-lane-tag">Egress prefix · ARM</div>
        <div class="sx-stats"><div class="stat"><div class="k">6<small>/8</small></div>
        <div class="l">addresses assigned</div></div>
        <div class="stat"><div class="k good">2</div><div class="l">spare · nodes the fleet can still add</div></div></div>
        {slots([".208", ".209", ".210", ".211", ".212", ".213", ".214", ".215"], 6)}
        <div class="legend"><span><i style="background:rgba(47,158,107,.35)"></i> In use</span>
        <span><i style="background:rgba(255,255,255,.7);border:1px solid var(--line)"></i> Spare</span>
        <span class="mono">4.166.55.208/29</span></div>
        {fresh("Just now")}</div>""",
    ],
    consequence=consequence("✅", "<strong>The fleet can recover on its own.</strong> One node is out "
                            "of the backend pool and a third is provisioning; two addresses are spare, "
                            "so the replacement will egress from inside the block your partners have "
                            "already allowlisted.", "ok"),
    flow=flowstrip("5.1", " MB/min", "avg 4.2 · peak 6.2 · 1-minute samples, last hour", net2, ACCENT),
)


# ------------------------------------------------------ state 3: cannot see

blind = card(
    "Azure Resource Manager did not answer — the fleet and the prefix are unread, not empty",
    "Egress path", fresh("Load balancer just now · monitor just now · fleet unavailable"),
    stations=[
        station("lb", LB_ICON, "Load balancer", "egproxy-ilb", "100", "% data path", "ok",
                "All probes passing"),
        station("vmss", VMSS_ICON, "Scale set", "egproxy-vmss", "", "", "off",
                "Not readable", blind=True),
        station("pip", PIP_ICON, "IP prefix", "egproxy-egress", "", "", "off",
                "Not readable", blind=True),
    ],
    pipes=[pipe(2, ":4750"), pipe(4, "—", "dead"), pipe(6, "—", "dead"), pipe(8, "—", "dead")],
    caps=CAPS,
    lanes=[
        f"""<div class="sx-lane a-lb"><div class="sx-lane-tag">Load balancer · Azure Monitor</div>
        {readout("Data path", pill("Available", "enforce"), "100% on the last 1-minute sample", lb1, GREEN)}
        {readout("Health probes", pill("All passing", "enforce"), "100% on the last 1-minute sample", lb1, GREEN)}
        {fresh("Just now")}</div>""",

        f"""<div class="sx-lane a-vmss"><div class="sx-lane-tag">Nodes · ARM instance view</div>
        <div class="note dim">The proxy scale set is not configured for this deployment, or is not
        readable by the portal's identity.</div>
        <div class="sx-gauges">
        {gauge("CPU", "0", "%", "avg 0 · peak 1", cpu1, VIOLET)}
        {gauge("Availability", "100", "%", "100.0% of the last hour's samples reported available", avail1, GREEN, "good")}
        </div>
        <div class="note dim" style="margin:0">Both series come from Azure Monitor, which answered.</div>
        </div>""",

        f"""<div class="sx-lane a-pip"><div class="sx-lane-tag">Egress prefix · ARM</div>
        <div class="note dim">No egress IP prefix is configured for this deployment, or it is not
        readable by the portal's identity. This is not the same as a prefix with nothing assigned.</div>
        </div>""",
    ],
    consequence=consequence("○", "<strong>Two of the four stages are unread.</strong> Nothing here "
                            "says the fleet is unhealthy or the prefix is empty — it says Resource "
                            "Manager did not answer, and the console will not guess on its behalf.",
                            "dim"),
    flow=flowstrip("0.2", " MB/min", "avg 0.2 · peak 0.8 · 1-minute samples, last hour", net1),
)


# ------------------------------------------------------------------------ css

SX_CSS = """
/* ==========================================================================================
   PROPOSED — the Runtime surface as one schematic.

   Replaces the five panels (fleet, egress addresses, network out, CPU, availability) with a
   single card that draws the path traffic actually takes: load balancer → scale set → IP
   prefix → partner. Every number the panels carried is still here; it is now attached to the
   stage it describes, so the constraint between stages is visible rather than inferred.

   Nothing below overrides portal.css. The tokens, glass treatment, pills, stats, pool chips,
   legend, banner and freshness stamp are all the existing ones.
   ========================================================================================== */

.sx-card { padding: 22px 24px 24px; }

.sx-head { display: flex; align-items: flex-start; justify-content: space-between;
           gap: 20px; margin-bottom: 20px; flex-wrap: wrap; }
.sx-title { margin-top: 3px; font-size: 14px; font-weight: 620; letter-spacing: -.01em;
            text-wrap: balance; max-width: 70ch; }

/* The process line and the instrument deck share one grid, so a lane sits under its station. */
.sx {
  display: grid;
  grid-template-columns:
    68px 60px minmax(0, 1fr) 60px minmax(0, 1.52fr) 60px minmax(0, 1.18fr) 60px 68px;
  grid-template-rows: auto 20px auto auto auto;
}

/* ------------------------------------------------------------------ stations */
.sx-station {
  grid-row: 1; position: relative; z-index: 1; min-width: 0;
  display: flex; flex-direction: column; gap: 9px;
  padding: 11px 13px 13px; border-radius: 16px;
  border: 1px solid rgba(255,255,255,.74);
  background: linear-gradient(135deg, rgba(255,255,255,.76), rgba(255,255,255,.48));
  box-shadow: 0 12px 26px -22px rgba(40,52,96,.65);
}
.sx-station.a-lb   { grid-column: 3; }
.sx-station.a-vmss { grid-column: 5; }
.sx-station.a-pip  { grid-column: 7; }

.sx-top { display: flex; align-items: center; gap: 9px; min-width: 0; }
.azicon { width: 26px; height: 26px; flex: none; display: block; }
.azicon.sm { width: 21px; height: 21px; }
.azicon svg { width: 100%; height: 100%; display: block; }

.sx-id { min-width: 0; display: flex; flex-direction: column; gap: 1px; }
.sx-tag { font-family: var(--mono); font-size: 8.5px; font-weight: 700;
          text-transform: uppercase; letter-spacing: .14em; color: var(--faint); }
.sx-name { font-family: var(--mono); font-size: 11px; color: var(--ink);
           overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }

.sx-read { display: flex; align-items: baseline; gap: 6px; flex-wrap: wrap; }
.sx-read .n { font-size: 22px; font-weight: 680; letter-spacing: -.02em; line-height: 1;
              font-variant-numeric: tabular-nums; }
.sx-read .u { font-size: 11px; color: var(--muted); }
.sx-sub { font-size: 11px; color: var(--muted); line-height: 1.45; }

/* "Cannot see" is a third state, never a healthy one — so it is hatched and unlit. */
.sx-station.blind { border-style: dashed; border-color: rgba(106,114,135,.38); box-shadow: none;
  background: repeating-linear-gradient(45deg,
      rgba(106,114,135,.06) 0 6px, rgba(255,255,255,.46) 6px 12px); }
.sx-station.blind .azicon { filter: grayscale(1); opacity: .5; }
.sx-blindnote { font-family: var(--mono); font-size: 10px; font-weight: 700;
                text-transform: uppercase; letter-spacing: .12em; color: var(--faint);
                padding: 6px 0 2px; }

/* ---------------------------------------------------------------------- lamps */
.sx-lamp { position: absolute; top: 13px; right: 13px; width: 9px; height: 9px;
           border-radius: 50%; flex: none; }
.sx-lamp.sm { position: static; width: 7px; height: 7px; box-shadow: none; }
.sx-lamp.ok   { background: var(--allow);  box-shadow: 0 0 0 3px rgba(47,158,107,.16); }
.sx-lamp.warn { background: var(--report); box-shadow: 0 0 0 3px rgba(217,131,36,.16); }
.sx-lamp.bad  { background: var(--deny);   box-shadow: 0 0 0 3px rgba(209,73,91,.16); }
.sx-lamp.off  { background: transparent; border: 1px solid rgba(106,114,135,.55); box-shadow: none; }
.sx-lamp.sm.ok, .sx-lamp.sm.warn, .sx-lamp.sm.bad { box-shadow: none; }

/* ---------------------------------------------------------------------- pipes */
.sx-pipe { grid-row: 1; position: relative; z-index: 2; }
.sx-duct {
  position: absolute; top: 18px; left: -7px; right: -7px; height: 13px;
  border-top: 1px solid rgba(28,35,51,.13); border-bottom: 1px solid rgba(28,35,51,.13);
  background-image:
    repeating-linear-gradient(90deg, rgba(91,141,239,.30) 0 5px, transparent 5px 13px),
    linear-gradient(180deg, rgba(255,255,255,.78), rgba(255,255,255,.34));
  background-size: 13px 100%, auto;
  animation: sx-flow 1.5s linear infinite;
}
.sx-duct::after { content: ""; position: absolute; right: -1px; top: 50%;
  transform: translateY(-50%); width: 0; height: 0;
  border-left: 6px solid rgba(28,35,51,.30);
  border-top: 5px solid transparent; border-bottom: 5px solid transparent; }
@keyframes sx-flow { from { background-position: 0 0, 0 0; } to { background-position: 13px 0, 0 0; } }

.sx-pipe.warn .sx-duct { background-image:
    repeating-linear-gradient(90deg, rgba(217,131,36,.42) 0 5px, transparent 5px 13px),
    linear-gradient(180deg, rgba(255,255,255,.78), rgba(255,255,255,.34)); }
.sx-pipe.bad .sx-duct { animation: none; background-image:
    repeating-linear-gradient(45deg, rgba(209,73,91,.30) 0 5px, transparent 5px 10px),
    linear-gradient(180deg, rgba(255,255,255,.78), rgba(255,255,255,.34));
  border-color: rgba(209,73,91,.35); }
.sx-pipe.dead .sx-duct { animation: none; background-image:
    repeating-linear-gradient(45deg, rgba(106,114,135,.16) 0 4px, transparent 4px 9px),
    linear-gradient(180deg, rgba(255,255,255,.60), rgba(255,255,255,.30)); }
.sx-pipe.dead .sx-duct::after { border-left-color: rgba(28,35,51,.14); }

.sx-chip { position: absolute; top: 37px; left: 50%; transform: translateX(-50%);
  white-space: nowrap; font-family: var(--mono); font-size: 7.5px; font-weight: 700;
  text-transform: uppercase; letter-spacing: .08em; color: var(--faint);
  padding: 2px 5px; border-radius: 999px; background: rgba(255,255,255,.82);
  border: 1px solid rgba(255,255,255,.9); }
.sx-pipe.warn .sx-chip { color: var(--report); background: rgba(217,131,36,.14); }
.sx-pipe.bad .sx-chip  { color: var(--deny);   background: rgba(209,73,91,.14); }

/* ----------------------------------------------------------------------- caps */
.sx-cap { grid-row: 1; display: flex; flex-direction: column; align-items: center;
          gap: 7px; padding-top: 9px; }
.sx-cap .lbl { font-family: var(--mono); font-size: 8px; font-weight: 700; line-height: 1.5;
               text-transform: uppercase; letter-spacing: .11em; color: var(--faint);
               text-align: center; }

/* ------------------------------------------------------- the instrument deck */
.sx-rule  { grid-column: 1 / -1; grid-row: 2; align-self: end; height: 1px; background: var(--line); }
.sx-riser { grid-row: 2; position: relative; }
.sx-riser::before { content: ""; position: absolute; left: 17px; top: 4px; bottom: 0;
                    border-left: 1px dashed rgba(28,35,51,.22); }

.sx-lane { grid-row: 3; min-width: 0; padding: 14px 1px 0;
           display: flex; flex-direction: column; gap: 12px; }
.sx-lane.a-lb   { grid-column: 3; }
.sx-lane.a-vmss { grid-column: 5; }
.sx-lane.a-pip  { grid-column: 7; }
.sx-lane-tag { font-family: var(--mono); font-size: 8.5px; font-weight: 700;
               text-transform: uppercase; letter-spacing: .14em; color: var(--faint); }
.sx-lane .freshness { margin-top: auto; }

.sx-ro { display: flex; flex-direction: column; gap: 5px; }
.sx-ro .hd { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
.sx-ro .hd .l { font-size: 11.5px; font-weight: 600; }
.sx-ro .dt { font-size: 10.5px; color: var(--muted); line-height: 1.45; }
/* The readout series live at 100 nearly always, so they are drawn as a line on a track: a filled
   chart pinned at its own maximum reads as a progress bar, which is a different claim. */
.sx-track { border-radius: 7px; padding: 0; overflow: hidden; color: var(--ink);
            border: 1px solid rgba(255,255,255,.62); background: rgba(255,255,255,.44); }
.sx-track svg { display: block; }
.sx-mini { color: var(--ink); }

.sx-nodes { display: flex; flex-direction: column; gap: 7px; }
.sx-node { display: flex; flex-direction: column; gap: 4px; min-width: 0;
           padding: 9px 11px; border-radius: 12px;
           border: 1px solid rgba(255,255,255,.62); background: rgba(255,255,255,.44); }
.sx-node .hd { display: flex; align-items: center; gap: 8px; min-width: 0; }
.sx-node .nm  { flex: 1; min-width: 0; font-family: var(--mono); font-size: 11px; font-weight: 600;
                overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.sx-node .sub { font-family: var(--mono); font-size: 9.5px; color: var(--faint);
                white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }

.sx-gauges { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 14px; }
.sx-gauge .n { margin-top: 3px; font-size: 21px; font-weight: 680; letter-spacing: -.02em;
               line-height: 1.05; font-variant-numeric: tabular-nums; }
.sx-gauge .n small { font-size: 11px; font-weight: 500; color: var(--muted); margin-left: 2px; }
.sx-gauge .n.good { color: var(--allow); }
.sx-gauge .n.warn { color: var(--report); }
.sx-gauge .n.bad  { color: var(--deny); }
.sx-gauge .dt { margin-top: 4px; font-size: 10.5px; color: var(--muted); line-height: 1.45; }

/* The sentence that belongs to the path, not to a stage — so it spans the deck. ADDED to the
   system: portal.css has one banner tone; a consequence that can be good, bad or unknown needs
   three, held on the same allow/report/deny ramp everything else on this console uses. */
.sx-conseq { grid-column: 1 / -1; grid-row: 4; margin-top: 20px;
  display: flex; gap: 11px; align-items: flex-start;
  padding: 13px 16px; border-radius: 13px; font-size: 12px; line-height: 1.55;
  color: var(--muted);
  border: 1px solid rgba(91,141,239,.28); background: rgba(91,141,239,.09); }
.sx-conseq strong { color: var(--ink); font-weight: 640; }
.sx-conseq .bico { flex: none; font-size: 13px; line-height: 1.35; }
.sx-conseq.bad { border-color: rgba(209,73,91,.30); background: rgba(209,73,91,.09); }
.sx-conseq.ok  { border-color: rgba(47,158,107,.30); background: rgba(47,158,107,.09); }
.sx-conseq.dim { border-style: dashed; border-color: rgba(106,114,135,.32);
                 background: rgba(106,114,135,.07); }
.sx-conseq.dim .bico { color: var(--faint); }

/* Two stats that must stay side by side in a narrow lane; the shared .stats wraps them. */
.sx-stats { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 14px; }
.sx-stats .k { font-size: 25px; font-weight: 680; letter-spacing: -.02em; line-height: 1.05;
               font-variant-numeric: tabular-nums; }
.sx-stats .k small { font-size: 12px; font-weight: 500; color: var(--muted); margin-left: 2px; }
.sx-stats .k.good { color: var(--allow); }
.sx-stats .k.bad  { color: var(--deny); }
.sx-stats .l { margin-top: 3px; font-size: 10.5px; color: var(--muted); line-height: 1.4; }

/* The trend recorder: the one number that belongs to the path rather than to any stage. */
.sx-flow { grid-column: 1 / -1; grid-row: 5; margin-top: 14px;
           padding: 13px 16px 9px; border-radius: 16px;
           border: 1px solid rgba(255,255,255,.62); background: rgba(255,255,255,.40); }
.sx-flow-head { display: flex; align-items: baseline; gap: 14px; flex-wrap: wrap; margin-bottom: 6px; }
.sx-flow-n { font-size: 19px; font-weight: 680; letter-spacing: -.02em;
             font-variant-numeric: tabular-nums; }
.sx-flow-n small { font-size: 11px; font-weight: 500; color: var(--muted); }
.sx-flow-d { font-size: 11px; color: var(--muted); }

/* --------------------------------------------------------------- narrow view */
/* Below the schematic's minimum useful width the path stops being drawable side by side, so
   each station drops to its own instruments and the ducts go away rather than shrink to noise. */
@media (max-width: 1100px) {
  .sx { display: flex; flex-direction: column; gap: 4px; }
  .sx-cap, .sx-pipe, .sx-riser, .sx-rule { display: none; }
  .sx-station.a-lb   { order: 1; } .sx-lane.a-lb   { order: 2; }
  .sx-station.a-vmss { order: 3; } .sx-lane.a-vmss { order: 4; }
  .sx-station.a-pip  { order: 5; } .sx-lane.a-pip  { order: 6; }
  .sx-conseq { order: 7; } .sx-flow { order: 8; }
  .sx-lane { padding-bottom: 8px; }
  .sx-lane .freshness { margin-top: 0; }
}

@media (prefers-reduced-motion: reduce) { .sx-duct { animation: none; } }

/* ----------------------------------------------- mockup-only page furniture */
.mk-note { max-width: 78ch; margin: 40px 0 14px; }
.mk-note h2 { margin: 0 0 6px; font-size: 15px; font-weight: 640; letter-spacing: -.01em; }
.mk-note p  { margin: 0; font-size: 12.5px; color: var(--muted); line-height: 1.6; }
.mk-flag { display: inline-flex; align-items: center; gap: 7px; margin-bottom: 10px;
           font-family: var(--mono); font-size: 8.5px; font-weight: 700;
           text-transform: uppercase; letter-spacing: .14em; color: var(--faint);
           padding: 5px 10px; border-radius: 999px;
           border: 1px dashed rgba(106,114,135,.40); background: rgba(255,255,255,.42); }
.mk-stack { display: flex; flex-direction: column; gap: 12px; margin-bottom: 34px; }
.mk-rat { display: grid; grid-template-columns: repeat(auto-fit, minmax(255px, 1fr));
          gap: 16px; margin-top: 18px; }
.mk-rat .card { padding: 17px 18px; }
.mk-rat h3 { margin: 8px 0 5px; font-size: 12.5px; font-weight: 640; }
.mk-rat p  { margin: 0; font-size: 11.5px; color: var(--muted); line-height: 1.6; }
.mk-rat ul { margin: 7px 0 0; padding-left: 17px; font-size: 11.5px; color: var(--muted);
             line-height: 1.65; }
"""


# ----------------------------------------------------------------------- page

def study(flag, title, body, blurb):
    return f"""<div class="mk-stack">
  <div><span class="mk-flag">{flag}</span>
  <div class="mk-note" style="margin:0 0 12px"><h2>{title}</h2><p>{blurb}</p></div></div>
  {body}
</div>"""


HTML = f"""<title>Runtime as one schematic — egress proxy console</title>
<style>
{PORTAL_CSS}
{SX_CSS}
</style>

<header class="shell">
  <div class="brand"><span class="chip">EG</span>
    <span class="wordmark">Egress<br>proxy<br>console</span></div>
  <nav class="tabs"><div class="tabgroup">
    <a class="tab" href="#">Overview</a><a class="tab" href="#">Rulesets</a>
    <a class="tab" href="#">Traffic</a><a class="tab" href="#">Lookup</a>
    <a class="tab" href="#">Platform</a><a class="tab" href="#" aria-current="page">Runtime</a>
  </div></nav>
  <div class="headerside"><div class="whoami"><span class="av">M</span>
    <span><span class="who">marnix@alanta.nl</span><br><span class="role">Platform team</span></span>
  </div></div>
</header>

<main>
  <div class="pagehead">
    <div><h1>Runtime</h1>
    <p>One path, four stages. Traffic arrives at the load balancer, crosses the proxy nodes and
    leaves from the addresses in the egress prefix — the panels used to say this separately.</p></div>
    {fresh("Just now")}
  </div>

  {nominal}

  <div class="mk-note">
    <h2>The same picture, read twice</h2>
    <p>A schematic earns its place only if it survives the states the panels handled well: a fleet
    in trouble, and a source that cannot be read at all. Both studies below use the same components
    as the card above — nothing is drawn specially for the bad day.</p>
  </div>

  {study("State study · a node is down", "Trouble at one stage, headroom at the next",
         degraded,
         "Everything red here is upstream: a node out of the backend pool, a probe failing, CPU "
         "climbing on what is left. The prefix is green with two addresses spare, so the "
         "replacement has somewhere to land — which is the reading the separate panels made you "
         "assemble yourself, and the reason the consequence line sits under the whole path rather "
         "than inside the prefix panel.")}

  {study("State study · partial outage", "ARM did not answer; Azure Monitor did",
         blind,
         "Unread is hatched and unlit, never green. The load-balancer stage still reports and the "
         "CPU and availability series still render — both come from Azure Monitor — while the two "
         "stages that depend on Resource Manager say plainly that they cannot see.")}

  <div class="mk-note"><h2>Notes on the design</h2></div>
  <div class="mk-rat">
    <div class="card">
      <span class="eyebrow">Layout</span>
      <h3>Stage above, instruments below</h3>
      <p>The process line carries one headline number and one lamp per stage. Everything the old
      panels held sits in a lane directly under its own stage, tied up to it by an instrument riser,
      so the column <em>is</em> the association — no legend, no cross-referencing.</p>
    </div>
    <div class="card">
      <span class="eyebrow">Where each panel went</span>
      <h3>Nothing was dropped</h3>
      <ul>
        <li>Fleet → the scale-set stage and its node lane</li>
        <li>Load-balancer rows → their own stage, promoted out of the fleet card's footer</li>
        <li>Egress addresses → the prefix stage, chips and consequence intact</li>
        <li>CPU, availability → gauges in the node lane, where the machine is</li>
        <li>Network out → the trend recorder across the foot of the path</li>
      </ul>
    </div>
    <div class="card">
      <span class="eyebrow">One addition</span>
      <h3>Sparklines on the load balancer</h3>
      <p>Data path and probe status already arrive as full one-hour series; only the latest sample
      was rendered. Drawing the series costs no new query and turns "66%" into "since when".</p>
    </div>
    <div class="card">
      <span class="eyebrow">Honesty</span>
      <h3>Three lamp states, not two</h3>
      <p>Lit green, lit amber or red, and unlit-with-hatching. A stage the console cannot read is
      never coloured, and every lane keeps its own freshness stamp because the stages are fed by
      different sources at different ages.</p>
    </div>
    <div class="card">
      <span class="eyebrow">Motion</span>
      <h3>Four duct states, and only one of them moves</h3>
      <p>Blue and moving is traffic passing. Amber and moving is passing but constrained — a full
      prefix caps growth, it does not stop today's requests, and the picture must not say otherwise.
      Red hatched and still is nothing passing; grey hatched and still is nothing known. Motion is
      the reading here, not decoration, and it is suppressed under
      <span class="mono">prefers-reduced-motion</span>.</p>
    </div>
    <div class="card">
      <span class="eyebrow">The line under the path</span>
      <h3>The consequence spans the deck</h3>
      <p>"Zero spare" is a fact about the prefix; "the fleet cannot grow" is a fact about the path.
      That sentence used to sit inside the address panel, where it read as a footnote to a number.
      Given the full width it becomes the card's conclusion — and it is the one element that changes
      tone across all three studies.</p>
    </div>
    <div class="card">
      <span class="eyebrow">Assets</span>
      <h3>Official Azure icons</h3>
      <p>Load Balancer, Virtual Machine Scale Set, Public IP Prefix and Virtual Network, inline SVG
      with namespaced gradient ids. Microsoft licenses these for architecture diagrams and
      documentation — worth confirming that reading covers a console surface before shipping.</p>
    </div>
  </div>

  <div class="mk-note" style="margin-bottom:0">
    <p><strong>Not shown:</strong> below 1100px the ducts and caps are dropped and each stage stacks
    above its own instruments — the path stops being drawable side by side well before it stops
    being readable. This page renders in the console's committed light world; it does not
    follow a dark theme, because <span class="mono">portal.css</span> sets
    <span class="mono">color-scheme: only light</span>.</p>
  </div>
</main>
"""

out = HERE / "runtime-schematic.html"
out.write_text(HTML)
print(f"wrote {out} ({out.stat().st_size / 1024:.0f} KB)")
