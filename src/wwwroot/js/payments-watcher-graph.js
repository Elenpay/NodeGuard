/*
 * Payments Watcher — framework-agnostic Lightning payment-route graph renderer.
 *
 * Port of LightningEye's React frontend (GraphCanvas / GraphNode / GraphEdge /
 * graphLayout / colorUtils / aliases / PaymentTraces) into one vanilla-JS module.
 * All UI strings translated ES -> EN.
 *
 * Why vanilla JS and not Blazor markup: NodeGuard's UI runs render-mode="Server",
 * so every DOM event round-trips over the SignalR circuit. Per-mousemove drag and
 * wheel/zoom would be laggy, and Blazor's DOM diff clobbers JS that mutates the same
 * subtree. So JS owns the ENTIRE canvas subtree; Blazor owns only the chrome
 * (date range, toggles, origin/destination selectors) and feeds this module JSON.
 *
 * Public API (attached to window.paymentsWatcher):
 *   render(containerId, graph, options)
 *     containerId : string id of an empty <div> Blazor rendered.
 *     graph       : { nodes:[{id,isOrigin,alias?,payments:[{id,status}]}],
 *                     channels:[{id,from,to,paymentId,paymentStatus,hopStatus?,
 *                                failureCode?,attemptIndex?,hopSequence?}] }
 *                   NOTE: camelCase. Blazor must serialize the PaymentGraph record
 *                   with a camelCase policy or the graph renders blank.
 *     options     : { showSuccess:bool, showFailed:bool,
 *                     dotNetRef?:DotNetObjectReference }  // for node-click callback
 *   Node click invokes dotNetRef.invokeMethodAsync('OnNodeSelected', nodeId) if given.
 */
(function () {
  'use strict';

  // ── Colour: red -> yellow -> green by success ratio (matches colorUtils.js) ──
  var RED = [226, 75, 74], YELLOW = [240, 190, 40], GREEN = [29, 158, 117];
  function mix(a, b, t) {
    return [Math.round(a[0] + t * (b[0] - a[0])),
            Math.round(a[1] + t * (b[1] - a[1])),
            Math.round(a[2] + t * (b[2] - a[2]))];
  }
  function colorByRatio(ratio) {
    var c = ratio < 0.5 ? mix(RED, YELLOW, ratio / 0.5)
                        : mix(YELLOW, GREEN, (ratio - 0.5) / 0.5);
    return 'rgb(' + c[0] + ',' + c[1] + ',' + c[2] + ')';
  }
  function ratioColors(payments, showSuccess, showFailed) {
    var relevant = payments.filter(function (p) {
      return (p.status === 'success' && showSuccess) || (p.status === 'failed' && showFailed);
    });
    if (relevant.length === 0) return { border: '#94a3b8', bg: '#f8fafc' };
    var ok = relevant.filter(function (p) { return p.status === 'success'; }).length;
    return { border: colorByRatio(ok / relevant.length) };
  }

  // ── Aliases (matches aliases.js) ─────────────────────────────────────────────
  function buildAliasMap(nodes) {
    var map = {};
    if (!nodes) return map;
    nodes.forEach(function (n) { if (n.alias && n.alias.trim()) map[n.id] = n.alias.trim(); });
    var noAlias = nodes.filter(function (n) { return !map[n.id]; });
    var origin = noAlias.find(function (n) { return n.isOrigin; });
    if (origin) map[origin.id] = '★';
    var rest = noAlias.filter(function (n) { return !n.isOrigin; }).map(function (n) { return n.id; }).sort();
    var LETTERS = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ';
    rest.forEach(function (id, i) { map[id] = i < LETTERS.length ? LETTERS[i] : 'N' + (i + 1); });
    return map;
  }
  function shortAlias(alias, max) {
    max = max || 6;
    if (!alias) return '?';
    return alias.length <= max ? alias : alias.slice(0, max) + '…';
  }
  function shortKey(id, head, tail) {
    head = head || 6; tail = tail || 4;
    if (!id) return '';
    return id.length <= head + tail + 1 ? id : id.slice(0, head) + '…' + id.slice(-tail);
  }

  // ── Layout (matches graphLayout.js) ──────────────────────────────────────────
  var NODE_W = 170, NODE_H = 64, COL_GAP = 400, ROW_GAP = 155, PAD_X = 16, PAD_Y = 20, NUM_COLS = 3;
  function computeLayout(nodes, channels) {
    if (!nodes || nodes.length === 0) return {};
    var origin = nodes.find(function (n) { return n.isOrigin; }) || nodes[0];
    var levels = {}; levels[origin.id] = 0;
    var rest = nodes.filter(function (n) { return n.id !== origin.id; });
    rest.forEach(function (n, i) { levels[n.id] = 1 + (i % NUM_COLS); });

    var byLevel = {};
    Object.keys(levels).forEach(function (id) {
      var lvl = levels[id]; (byLevel[lvl] = byLevel[lvl] || []).push(id);
    });
    Object.keys(byLevel).forEach(function (lvl) { byLevel[lvl] = orderColumn(byLevel[lvl], channels); });

    var maxRows = Math.max.apply(null, Object.keys(byLevel).map(function (k) { return byLevel[k].length; }));
    var totalH = maxRows * ROW_GAP, positions = {};
    Object.keys(byLevel).forEach(function (lvl) {
      var ids = byLevel[lvl];
      var x = PAD_X + parseInt(lvl, 10) * COL_GAP;
      var startY = PAD_Y + (totalH - ids.length * ROW_GAP) / 2;
      ids.forEach(function (id, i) { positions[id] = { x: x, y: startY + i * ROW_GAP, w: NODE_W, h: NODE_H }; });
    });
    return positions;
  }
  function orderColumn(ids, channels) {
    if (ids.length <= 1) return ids;
    var inCol = {}; ids.forEach(function (id) { inCol[id] = true; });
    var adj = {}; ids.forEach(function (id) { adj[id] = []; });
    channels.forEach(function (ch) {
      if (inCol[ch.from] && inCol[ch.to] && ch.from !== ch.to) {
        if (adj[ch.from].indexOf(ch.to) < 0) adj[ch.from].push(ch.to);
        if (adj[ch.to].indexOf(ch.from) < 0) adj[ch.to].push(ch.from);
      }
    });
    var visited = {}, ordered = [];
    ids.slice().sort(function (a, b) { return adj[a].length - adj[b].length; }).forEach(function (start) {
      if (visited[start]) return;
      var stack = [start];
      while (stack.length) {
        var n = stack.pop();
        if (visited[n]) continue;
        visited[n] = true; ordered.push(n);
        adj[n].forEach(function (nb) { if (!visited[nb]) stack.push(nb); });
      }
    });
    return ordered;
  }
  function canvasSize(positions) {
    var keys = Object.keys(positions);
    if (keys.length === 0) return { width: 800, height: 400 };
    var maxX = 0, maxY = 0;
    keys.forEach(function (k) { var p = positions[k]; maxX = Math.max(maxX, p.x + p.w); maxY = Math.max(maxY, p.y + p.h); });
    return { width: maxX + 30, height: maxY + 30 };
  }

  // ── Edge geometry (matches GraphEdge.jsx) ────────────────────────────────────
  function borderPoint(cx, cy, hw, hh, tx, ty) {
    var dx = tx - cx, dy = ty - cy;
    if (!dx && !dy) return { x: cx, y: cy };
    var s = Math.min(hw / Math.abs(dx || 1e-9), hh / Math.abs(dy || 1e-9)) * 0.95;
    return { x: cx + dx * s, y: cy + dy * s };
  }

  var SVG_NS = 'http://www.w3.org/2000/svg';
  function el(tag, attrs) {
    var e = document.createElement(tag);
    if (attrs) Object.keys(attrs).forEach(function (k) { e.setAttribute(k, attrs[k]); });
    return e;
  }
  function svgEl(tag, attrs) {
    var e = document.createElementNS(SVG_NS, tag);
    if (attrs) Object.keys(attrs).forEach(function (k) { e.setAttribute(k, attrs[k]); });
    return e;
  }

  // ── Render ────────────────────────────────────────────────────────────────
  function render(containerId, graph, options) {
    options = options || {};
    var showSuccess = options.showSuccess !== false;
    var showFailed = options.showFailed !== false;
    var dotNetRef = options.dotNetRef || null;
    var root = document.getElementById(containerId);
    if (!root) { console.error('[paymentsWatcher] container not found:', containerId); return; }

    // Preserve drag/zoom across re-renders (toggle changes) via element state.
    var state = root.__pwState || { moved: {}, zoom: 1, selected: null };
    root.__pwState = state;
    root.innerHTML = '';

    if (!graph || !graph.nodes || graph.nodes.length === 0) {
      root.appendChild(centered('⚡', 'No graph data.'));
      return;
    }

    var aliasMap = buildAliasMap(graph.nodes);
    var auto = computeLayout(graph.nodes, graph.channels);
    var positions = {};
    Object.keys(auto).forEach(function (id) {
      positions[id] = state.moved[id] ? Object.assign({}, auto[id], state.moved[id]) : auto[id];
    });
    var size = canvasSize(positions);

    // Aggregate visible channels per (from|to) for colour + arrow direction offset.
    var visChannels = graph.channels.filter(function (ch) {
      return (ch.paymentStatus === 'success' && showSuccess) || (ch.paymentStatus === 'failed' && showFailed);
    });
    var chanMap = {};
    visChannels.forEach(function (ch) {
      var key = ch.from + '|' + ch.to;
      if (!chanMap[key]) chanMap[key] = { key: key, from: ch.from, to: ch.to, ok: 0, fail: 0 };
      if (ch.paymentStatus === 'success') chanMap[key].ok++; else chanMap[key].fail++;
    });
    var edges = Object.keys(chanMap).map(function (k) { return chanMap[k]; });
    edges.forEach(function (e) { e.split = !!chanMap[e.to + '|' + e.from]; });

    // ── Zoom controls ──
    // Fixed 60vh viewport (not max-height): the graph pane keeps a stable size even
    // when the layout is small, instead of collapsing to the content height. The height
    // lives here, on the in-flow box, because the scroller below is taken out of flow.
    var wrap = el('div', { style: 'position:relative;height:60vh;' });
    var zoomBox = el('div', { style: 'position:absolute;top:12px;right:16px;z-index:20;display:flex;flex-direction:column;gap:6px;' });
    function zbtn(label, title, fn, small) {
      var b = el('button', { title: title, type: 'button',
        style: 'width:34px;height:34px;border-radius:8px;cursor:pointer;border:1px solid #e2e8f0;background:#fff;color:#475569;font-size:' + (small ? 14 : 18) + 'px;font-weight:700;display:flex;align-items:center;justify-content:center;box-shadow:0 1px 3px rgba(0,0,0,0.08);' });
      b.textContent = label;
      b.addEventListener('click', fn);
      return b;
    }
    // Absolutely positioned on purpose: MainLayout's <main> is a `flex:1` item with no
    // `min-width:0`, so its automatic minimum size is its min-content width. An in-flow
    // scroller still contributes the stage's width to that (overflow:auto only zeroes
    // the automatic minimum size of the flex item that carries it — <main> — not of a
    // descendant), so a node dragged right would widen <main> and stretch the whole
    // toolbar. Out of flow, the stage contributes nothing to the page's intrinsic width.
    var scroller = el('div', { style: 'position:absolute;top:0;left:0;right:0;bottom:0;overflow:auto;padding:20px 18px;box-sizing:border-box;' });
    var stage = el('div', { style: 'position:relative;width:' + size.width + 'px;height:' + size.height + 'px;min-width:' + size.width + 'px;transform-origin:top left;' });
    function applyZoom() { stage.style.transform = 'scale(' + state.zoom + ')'; }
    zoomBox.appendChild(zbtn('+', 'Zoom in', function () { state.zoom = Math.min(2, +(state.zoom + 0.15).toFixed(2)); applyZoom(); }));
    zoomBox.appendChild(zbtn('−', 'Zoom out', function () { state.zoom = Math.max(0.4, +(state.zoom - 0.15).toFixed(2)); applyZoom(); }));
    zoomBox.appendChild(zbtn('⟳', 'Reset', function () { state.zoom = 1; applyZoom(); }, true));
    applyZoom();

    // ── Edges (SVG) ──
    // Nodes are absolutely positioned HTML, so dragging one past the initial layout
    // bounds just overflows the stage. SVG children are not so lucky: the UA style
    // sheet clips the root <svg> to its width/height, which cut lines and arrowheads
    // mid-canvas. Hence `overflow:visible` (stops the clipping) *and* drawEdges()
    // growing the svg/stage (makes the overflowing area reachable by the scroller —
    // ink outside an svg's box contributes no scrollable overflow on its own).
    var svg = svgEl('svg', { width: size.width, height: size.height,
      style: 'position:absolute;top:0;left:0;overflow:visible;pointer-events:none;' });
    stage.appendChild(svg);
    drawEdges(positions);

    // ── Nodes ──
    var drag = null;
    graph.nodes.forEach(function (node) {
      var pos = positions[node.id];
      if (!pos) return;
      var border = ratioColors(node.payments, showSuccess, showFailed).border;
      var rgb = border.match(/\d+/g);
      var softBg = rgb ? 'rgba(' + rgb[0] + ',' + rgb[1] + ',' + rgb[2] + ',0.12)' : '#eef1f4';
      var strongBg = rgb ? 'rgb(' + rgb[0] + ',' + rgb[1] + ',' + rgb[2] + ')' : '#0f6e56';
      var vis = node.payments.filter(function (p) {
        return (p.status === 'success' && showSuccess) || (p.status === 'failed' && showFailed);
      });
      var ok = vis.filter(function (p) { return p.status === 'success'; }).length;
      var fail = vis.length - ok;
      var sel = state.selected === node.id;

      var box = el('div', {
        title: (node.isOrigin ? 'Origin' : (aliasMap[node.id] || '?')) + '\n' + node.id + '\n(click to view and copy the pubkey)',
        style: 'position:absolute;left:' + pos.x + 'px;top:' + pos.y + 'px;width:' + pos.w + 'px;height:' + pos.h + 'px;' +
          'display:flex;align-items:center;gap:13px;padding:0 12px;box-sizing:border-box;background:#fff;' +
          'border:' + (sel ? 2 : 1) + 'px solid ' + (sel ? border : '#cbd5e1') + ';border-radius:12px;' +
          'cursor:pointer;user-select:none;z-index:' + (sel ? 10 : 5) + ';transition:border-color .15s;'
      });
      var badge = el('div', {
        style: 'width:64px;height:36px;flex-shrink:0;border-radius:10px;background:' + (node.isOrigin ? strongBg : softBg) + ';' +
          'color:' + (node.isOrigin ? '#fff' : border) + ';display:flex;align-items:center;justify-content:center;' +
          'font-size:12.5px;font-weight:700;padding:0 6px;box-sizing:border-box;white-space:nowrap;overflow:hidden;'
      });
      badge.textContent = node.isOrigin ? 'origin' : shortAlias(aliasMap[node.id]);
      box.appendChild(badge);

      var info = el('div', { style: 'min-width:0;flex:0 1 auto;' });
      var key = el('div', { style: 'font-family:monospace;font-size:10.5px;color:#94a3b8;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;' });
      key.textContent = shortKey(node.id, 4, 4);
      var counts = el('div', { style: 'display:flex;gap:8px;margin-top:3px;align-items:center;font-size:10.5px;' });
      if (ok > 0) { var s1 = el('span', { style: 'color:#1D9E75;font-weight:600;' }); s1.textContent = '● ' + ok; counts.appendChild(s1); }
      if (fail > 0) { var s2 = el('span', { style: 'color:#E24B4A;font-weight:600;' }); s2.textContent = '● ' + fail; counts.appendChild(s2); }
      if (vis.length === 0) { var s3 = el('span', { style: 'color:#94a3b8;' }); s3.textContent = 'no payments'; counts.appendChild(s3); }
      info.appendChild(key); info.appendChild(counts); box.appendChild(info);

      box.addEventListener('mousedown', function (ev) {
        ev.preventDefault(); ev.stopPropagation();
        // Anchor on state.moved, not on `pos`: a drag doesn't re-render, so `pos`
        // still holds the position this node had when the graph was last drawn and
        // a second drag would snap the node back there.
        var cur = state.moved[node.id] || pos;
        drag = { id: node.id, sx: ev.clientX, sy: ev.clientY, x0: cur.x, y0: cur.y, moved: false };
        window.addEventListener('mousemove', onMove);
        window.addEventListener('mouseup', onUp);
      });
      box.addEventListener('click', function () {
        if (drag && drag.moved) return;
        state.selected = state.selected === node.id ? null : node.id;
        if (dotNetRef) dotNetRef.invokeMethodAsync('OnNodeSelected', state.selected);
        render(containerId, graph, options); // cheap re-render to reflect selection border
      });

      function onMove(ev) {
        if (!drag) return;
        var dx = (ev.clientX - drag.sx) / state.zoom, dy = (ev.clientY - drag.sy) / state.zoom;
        if (Math.abs(dx) > 2 || Math.abs(dy) > 2) drag.moved = true;
        state.moved[drag.id] = { x: Math.max(0, drag.x0 + dx), y: Math.max(0, drag.y0 + dy) };
        box.style.left = state.moved[drag.id].x + 'px';
        box.style.top = state.moved[drag.id].y + 'px';
        // Redraw edges live so arrows follow the dragged node.
        drawEdges();
      }
      function onUp() {
        window.removeEventListener('mousemove', onMove);
        window.removeEventListener('mouseup', onUp);
        var d = drag; setTimeout(function () { if (drag === d) drag = null; }, 0);
      }
      stage.appendChild(box);
    });

    // Grow the stage and the SVG viewport to cover the current node positions, never
    // shrinking below the auto layout so the scroll position doesn't jump when a node
    // is dragged back towards the origin.
    function fitStage(np) {
      var s = canvasSize(np);
      var w = Math.max(s.width, size.width), h = Math.max(s.height, size.height);
      stage.style.width = w + 'px';
      stage.style.minWidth = w + 'px';
      stage.style.height = h + 'px';
      svg.setAttribute('width', w);
      svg.setAttribute('height', h);
    }

    function drawEdges(np) {
      // Recompute positions from state.moved and rebuild the SVG in place.
      if (!np) {
        np = {};
        Object.keys(auto).forEach(function (id) {
          np[id] = state.moved[id] ? Object.assign({}, auto[id], state.moved[id]) : auto[id];
        });
      }
      fitStage(np);
      while (svg.firstChild) svg.removeChild(svg.firstChild);
      edges.forEach(function (e) {
        var fp = np[e.from], tp = np[e.to];
        if (!fp || !tp) return;
        var fcx = fp.x + fp.w / 2, fcy = fp.y + fp.h / 2, tcx = tp.x + tp.w / 2, tcy = tp.y + tp.h / 2;
        var sp = borderPoint(fcx, fcy, fp.w / 2, fp.h / 2, tcx, tcy);
        var GAP = 7, ep = borderPoint(tcx, tcy, tp.w / 2 + GAP, tp.h / 2 + GAP, fcx, fcy);
        if (e.split) {
          var dx = ep.x - sp.x, dy = ep.y - sp.y, len = Math.hypot(dx, dy) || 1, SEP = 6;
          var ox = -dy / len * SEP, oy = dx / len * SEP;
          sp = { x: sp.x + ox, y: sp.y + oy }; ep = { x: ep.x + ox, y: ep.y + oy };
        }
        var total = e.ok + e.fail, color = colorByRatio(total === 0 ? 0 : e.ok / total);
        var mid = 'pw-arrow-' + e.key.replace(/[^a-zA-Z0-9]/g, '_');
        var defs = svgEl('defs');
        var marker = svgEl('marker', { id: mid, markerWidth: 7, markerHeight: 7, refX: 5, refY: 3.5, orient: 'auto' });
        marker.appendChild(svgEl('polygon', { points: '0,0 7,3.5 0,7', fill: color }));
        defs.appendChild(marker); svg.appendChild(defs);
        svg.appendChild(svgEl('line', { x1: sp.x, y1: sp.y, x2: ep.x, y2: ep.y, stroke: color,
          'stroke-width': 1.8, 'stroke-linecap': 'round', 'marker-end': 'url(#' + mid + ')', opacity: 0.9 }));
      });
    }

    scroller.appendChild(stage);
    wrap.appendChild(zoomBox);
    wrap.appendChild(scroller);
    root.appendChild(wrap);

    // ── Legend ──
    var legend = el('div', { style: 'padding:12px 4px 4px;display:flex;gap:18px;flex-wrap:wrap;align-items:center;' });
    legend.innerHTML =
      '<div style="display:flex;align-items:center;gap:8px;">' +
      '<span style="font-size:12px;color:#E24B4A;font-weight:600;">Failure</span>' +
      '<div style="width:160px;height:12px;border-radius:6px;background:linear-gradient(to right,#E24B4A 0%,#F0BE28 50%,#1D9E75 100%);border:1px solid #cbd5e1;"></div>' +
      '<span style="font-size:12px;color:#1D9E75;font-weight:600;">Success</span></div>' +
      '<span style="font-size:12px;color:#64748b;">Each channel\'s colour indicates its success ratio</span>' +
      '<span style="font-size:12px;color:#94a3b8;">Click a node → view and copy its pubkey</span>';
    root.appendChild(legend);

    // ── Payment traces ──
    root.appendChild(buildTraces(graph, aliasMap, showSuccess, showFailed, dotNetRef));
  }

  // ── Payment traces (matches PaymentTraces.jsx) ───────────────────────────────
  var SEG = { success: '#1D9E75', ok: '#C4841A', failed_here: '#E24B4A', unreached: '#B4B2A9', failed: '#E24B4A' };
  function buildTraces(graph, aliasMap, showSuccess, showFailed, dotNetRef) {
    var byAttempt = {};
    graph.channels.forEach(function (ch) {
      var key = ch.paymentId + '#' + (ch.attemptIndex || 0);
      if (!byAttempt[key]) byAttempt[key] = { paymentId: ch.paymentId, attemptIndex: ch.attemptIndex || 0, paymentStatus: ch.paymentStatus, hops: [] };
      byAttempt[key].hops.push(ch);
    });
    var traces = Object.keys(byAttempt).map(function (k) {
      var t = byAttempt[k];
      t.hops.sort(function (a, b) { return (a.hopSequence || 0) - (b.hopSequence || 0); });
      t.origin = t.hops[0] ? t.hops[0].from : null;
      var fc = t.hops.find(function (h) { return h.failureCode; });
      t.failureCode = fc ? fc.failureCode : null;
      return t;
    }).sort(function (a, b) { return a.paymentId.localeCompare(b.paymentId) || a.attemptIndex - b.attemptIndex; });

    var visible = traces.filter(function (t) {
      return (t.paymentStatus === 'success' && showSuccess) || (t.paymentStatus === 'failed' && showFailed);
    });

    var container = el('div', { style: 'margin-top:10px;background:#fff;border:1px solid #cbd5e1;border-radius:12px;padding:16px 18px;' });
    var title = el('div', { style: 'font-size:13px;font-weight:700;color:#334155;margin-bottom:12px;' });
    title.textContent = 'Payment traces';
    container.appendChild(title);
    if (visible.length === 0) { container.style.display = 'none'; return container; }

    var list = el('div', { style: 'display:flex;flex-direction:column;gap:8px;' });
    var alias = function (id) { return aliasMap[id] || '?'; };

    // Pagination (10 per page).
    var page = 0, PER = 10, totalPages = Math.ceil(visible.length / PER);
    function renderPage() {
      list.innerHTML = '';
      visible.slice(page * PER, page * PER + PER).forEach(function (t) {
        var failed = t.paymentStatus === 'failed';
        var row = el('div', { style: 'display:flex;align-items:center;gap:14px;flex-wrap:wrap;padding:10px 12px;border-radius:10px;background:#fafbfc;border:1px solid #eef1f4;' });
        var meta = el('div', { style: 'min-width:148px;display:flex;flex-direction:column;gap:4px;' });
        var hash = el('span', { title: 'Click to copy the payment hash', style: 'font-family:monospace;font-size:12px;color:#334155;cursor:pointer;' });
        hash.textContent = t.paymentId;
        hash.addEventListener('click', function () { if (navigator.clipboard) navigator.clipboard.writeText(t.paymentId); });
        var tag = el('span', { style: 'font-size:11px;padding:2px 8px;border-radius:8px;width:fit-content;background:' + (failed ? '#FCEBEB' : '#E1F5EE') + ';color:' + (failed ? '#A32D2D' : '#0F6E56') + ';' });
        tag.textContent = failed ? (t.attemptIndex > 0 ? 'failed · attempt ' + (t.attemptIndex + 1) : 'failed') : 'success';
        meta.appendChild(hash); meta.appendChild(tag); row.appendChild(meta);

        var path = el('div', { style: 'display:flex;align-items:center;flex:1;min-width:0;' });
        path.appendChild(hopPill(shortAlias(alias(t.origin)), failed ? 'ok' : 'success', t.origin, alias(t.origin), dotNetRef));
        t.hops.forEach(function (hop) {
          var tone = hop.hopStatus || (failed ? 'failed' : 'success');
          var seg = el('div', { style: 'display:flex;align-items:center;flex:1;min-width:24px;' });
          var line = el('span', { style: 'flex:1;min-width:14px;height:' + (tone === 'unreached' ? '0' : '2.5px') + ';background:' + (tone === 'unreached' ? 'transparent' : SEG[tone]) + ';border-top:' + (tone === 'unreached' ? '2px dashed ' + SEG.unreached : 'none') + ';' });
          seg.appendChild(line);
          seg.appendChild(hopPill(shortAlias(alias(hop.to)), tone, hop.to, alias(hop.to), dotNetRef));
          path.appendChild(seg);
        });
        if (t.failureCode) {
          var code = el('span', { style: 'font-family:monospace;font-size:11px;color:#A32D2D;background:#FCEBEB;padding:3px 9px;border-radius:8px;margin-left:12px;flex-shrink:0;' });
          code.textContent = t.failureCode; path.appendChild(code);
        }
        row.appendChild(path);
        list.appendChild(row);
      });
      pager.textContent = 'Page ' + (page + 1) + ' of ' + totalPages;
      prev.disabled = page === 0; next.disabled = page >= totalPages - 1;
    }
    container.appendChild(list);

    var nav = el('div', { style: 'display:flex;align-items:center;justify-content:center;margin-top:14px;gap:14px;' });
    var prev = pageBtn('← Previous', function () { if (page > 0) { page--; renderPage(); } });
    var pager = el('span', { style: 'font-size:12px;color:#64748b;font-weight:600;' });
    var next = pageBtn('Next →', function () { if (page < totalPages - 1) { page++; renderPage(); } });
    nav.appendChild(prev); nav.appendChild(pager); nav.appendChild(next);
    container.appendChild(nav);
    renderPage();
    return container;
  }

  function hopPill(label, tone, nodeId, fullAlias, dotNetRef) {
    var dim = tone === 'unreached', failed = tone === 'failed_here';
    var pill = el('div', {
      title: fullAlias + '\n' + nodeId,
      style: 'position:relative;width:56px;height:26px;flex-shrink:0;border-radius:13px;display:flex;align-items:center;justify-content:center;' +
        'font-size:11px;font-weight:600;cursor:pointer;padding:0 6px;box-sizing:border-box;white-space:nowrap;overflow:hidden;' +
        'background:' + (failed ? '#E24B4A' : dim ? '#f8fafc' : '#fff') + ';border:1.5px solid ' + (SEG[tone] || '#B4B2A9') + ';' +
        'color:' + (failed ? '#fff' : dim ? '#94a3b8' : (SEG[tone] || '#475569')) + ';opacity:' + (dim ? 0.6 : 1) + ';'
    });
    pill.textContent = label;
    pill.addEventListener('click', function () { if (dotNetRef && nodeId) dotNetRef.invokeMethodAsync('OnNodeSelected', nodeId); });
    return pill;
  }

  function pageBtn(label, fn) {
    var b = el('button', { type: 'button', style: 'padding:6px 14px;border-radius:6px;font-size:12px;cursor:pointer;border:1px solid #cbd5e1;background:#fff;color:#475569;' });
    b.textContent = label; b.addEventListener('click', fn);
    return b;
  }

  function centered(icon, text) {
    var d = el('div', { style: 'display:flex;flex-direction:column;align-items:center;justify-content:center;min-height:360px;text-align:center;padding:20px;' });
    var i = el('div', { style: 'font-size:48px;' }); i.textContent = icon;
    var p = el('p', { style: 'color:#94a3b8;margin-top:12px;' }); p.textContent = text;
    d.appendChild(i); d.appendChild(p);
    return d;
  }

  window.paymentsWatcher = { render: render };
})();
