const el = (id) => document.getElementById(id);
const int = new Intl.NumberFormat(undefined, { maximumFractionDigits: 0 });
const dec = new Intl.NumberFormat(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const SVG = "http://www.w3.org/2000/svg";

function node(tag, attrs, text) {
  const n = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) n.setAttribute(k, v);
  if (text !== undefined) n.textContent = text;
  return n;
}

function svgNode(tag, attrs) {
  const n = document.createElementNS(SVG, tag);
  for (const [k, v] of Object.entries(attrs || {})) n.setAttribute(k, v);
  return n;
}

function showError(message) {
  const box = el("error");
  box.textContent = message;
  box.hidden = !message;
}

function renderKpis(k) {
  const target = el("kpis");
  target.replaceChildren();
  const cards = [
    ["Tickets", int.format(k.tickets)],
    ["Avg operational cost", k.avgCost == null ? "—" : dec.format(k.avgCost)],
    ["Avg CSAT", k.avgCsat == null ? "—" : dec.format(k.avgCsat)],
    ["Avg sentiment", k.avgSentiment == null ? "—" : dec.format(k.avgSentiment)],
    ["First-time resolution", k.firstTimeResolutionRate == null ? "—" : `${(k.firstTimeResolutionRate * 100).toFixed(1)}%`]
  ];
  for (const [label, value] of cards) {
    const card = node("div", { class: "kpi" });
    card.append(node("div", { class: "value" }, value), node("div", { class: "label" }, label));
    target.append(card);
  }
}

function renderDaily(points) {
  const host = el("daily");
  host.replaceChildren();
  if (!points.length) {
    host.append(node("p", { class: "empty" }, "No tickets in this range."));
    return;
  }

  const w = 1000, h = 220, padX = 44, padY = 18;
  const max = Math.max(...points.map((p) => p.tickets), 1);
  const stepX = points.length > 1 ? (w - padX * 2) / (points.length - 1) : 0;
  const x = (i) => padX + i * stepX;
  const y = (v) => h - padY - (v / max) * (h - padY * 2);

  const svg = svgNode("svg", { viewBox: `0 0 ${w} ${h}`, preserveAspectRatio: "none", role: "img" });
  const title = svgNode("title", {});
  title.textContent = "Tickets per day";
  svg.append(title);

  for (let g = 0; g <= 2; g++) {
    const value = (max / 2) * g;
    svg.append(svgNode("line", {
      x1: padX, x2: w - padX, y1: y(value), y2: y(value), stroke: "#262b36", "stroke-width": 1
    }));
    const label = svgNode("text", { x: 6, y: y(value) + 3, class: "axis" });
    label.textContent = int.format(Math.round(value));
    svg.append(label);
  }

  const line = points.map((p, i) => `${i === 0 ? "M" : "L"}${x(i).toFixed(1)},${y(p.tickets).toFixed(1)}`).join(" ");
  svg.append(svgNode("path", { class: "area", d: `${line} L${x(points.length - 1)},${h - padY} L${padX},${h - padY} Z` }));
  svg.append(svgNode("path", { d: line }));

  for (const i of [0, points.length - 1]) {
    if (i < 0) continue;
    const label = svgNode("text", { x: x(i), y: h - 3, class: "axis", "text-anchor": i === 0 ? "start" : "end" });
    label.textContent = points[i].date;
    svg.append(label);
  }
  host.append(svg);
}

function renderBars(targetId, slices) {
  const host = el(targetId);
  host.replaceChildren();
  if (!slices.length) {
    host.append(node("p", { class: "empty" }, "No data."));
    return;
  }
  const max = Math.max(...slices.map((s) => s.tickets), 1);
  for (const slice of slices) {
    const row = node("div", { class: "bar-row" });
    const track = node("div", { class: "track" });
    track.append(node("div", { class: "fill", style: `width:${(slice.tickets / max) * 100}%` }));
    row.append(node("div", { class: "name" }, slice.label), track,
      node("div", { class: "num" }, int.format(slice.tickets)));
    host.append(row);
  }
}

async function load(event) {
  event?.preventDefault();
  const button = el("apply");
  button.disabled = true;
  showError("");
  const started = performance.now();

  try {
    // The picker shows an inclusive end date; the API treats 'to' as exclusive.
    const toExclusive = new Date(`${el("to").value}T00:00:00Z`);
    toExclusive.setUTCDate(toExclusive.getUTCDate() + 1);

    const params = new URLSearchParams({
      from: el("from").value,
      to: toExclusive.toISOString().slice(0, 10)
    });
    if (el("region").value) params.set("region", el("region").value);
    if (el("priority").value) params.set("priority", el("priority").value);

    const response = await fetch(`/api/dashboard?${params}`);
    if (!response.ok) {
      const problem = await response.json().catch(() => null);
      throw new Error(problem?.detail || `Request failed (${response.status}).`);
    }

    const data = await response.json();
    renderKpis(data.kpis);
    renderDaily(data.daily);
    renderBars("byRegion", data.byRegion);
    renderBars("byPriority", data.byPriority);
    el("timing").textContent = `${Math.round(performance.now() - started)} ms`;
  } catch (error) {
    showError(error.message);
  } finally {
    button.disabled = false;
  }
}

async function init() {
  fetch("/api/me").then((r) => r.json()).then((me) => {
    if (me?.name) el("who").textContent = `Signed in as ${me.name}`;
  }).catch(() => {});

  try {
    const range = await fetch("/api/range").then((r) => r.json());
    if (range.min && range.max) {
      el("from").value = range.min;
      el("to").value = range.max;
      el("from").min = range.min;
      el("to").max = range.max;
    }
  } catch {
    showError("Could not reach Lakebase. The compute may be resuming — try again in a moment.");
    return;
  }
  await load();
}

el("filters").addEventListener("submit", load);
init();
