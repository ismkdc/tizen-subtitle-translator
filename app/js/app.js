(function () {
  'use strict';

  var API = 'http://127.0.0.1:18765';
  var VETRO = 'http://127.0.0.1:8099/exec';
  var HELPER_START = "systemctl is-active --quiet subtitle-helper || (systemctl reset-failed subtitle-helper 2>/dev/null; systemd-run --unit=subtitle-helper --collect /usr/bin/dotnet /opt/usr/subtitle-helper/SubtitleHelper.dll)";

  var DEFAULT_CONFIG = {
    region: { x: 0.208, y: 0.861, w: 0.583, h: 0.107 },
    ocr: { threshold: 155, minConfidence: 72 },
    style: { size: 34, bottom: 1000, opacity: 1, color: '#FFFFFF' },
    translate: { source: 'EN', target: 'TR' }
  };

  var S = {
    helper: false,
    config: clone(DEFAULT_CONFIG),
    running: false,
    last: {},
    capture: null,
    key: false,
    error: null,
    view: 'hub',
    busy: false,
    booting: true,
    started: 0
  };

  function clone(o) { return JSON.parse(JSON.stringify(o)); }
  function $(id) { return document.getElementById(id); }
  function qsa(sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); }
  function clamp(v, a, b) { return Math.max(a, Math.min(b, v)); }
  function round2(v) { return Math.round(v * 100) / 100; }
  function round3(v) { return Math.round(v * 1000) / 1000; }
  function delay(ms) { return new Promise(function (r) { setTimeout(r, ms); }); }
  function text(v) { return v == null ? '' : String(v); }

  function withTimeout(p, ms) {
    return new Promise(function (resolve, reject) {
      var t = setTimeout(function () { reject(new Error('timeout')); }, ms);
      p.then(function (v) { clearTimeout(t); resolve(v); },
             function (e) { clearTimeout(t); reject(e); });
    });
  }

  function api(path, opts) {
    opts = opts || {};
    var init = { method: opts.method || 'GET', headers: {} };
    if (opts.body != null) {
      init.body = typeof opts.body === 'string' ? opts.body : JSON.stringify(opts.body);
      init.headers['Content-Type'] = 'application/json';
    }
    return withTimeout(fetch(API + path, init).then(function (res) {
      return res.text().then(function (body) {
        var data = null;
        try { data = JSON.parse(body); } catch (e) { /* not json */ }
        if (!res.ok) throw new Error((data && data.error) || ('HTTP ' + res.status));
        return data;
      });
    }), opts.timeout || 4000);
  }

  function vetroStartHelper() {
    try {
      fetch(VETRO, {
        method: 'POST',
        mode: 'no-cors',
        headers: { 'Content-Type': 'text/plain' },
        body: JSON.stringify({ cmd: HELPER_START })
      }).catch(function () {});
    } catch (e) { /* ignore */ }
  }

  function ensureHelper() {
    return api('/health', { timeout: 1400 }).then(function () {
      S.helper = true;
      return true;
    }).catch(function () {
      vetroStartHelper();
      return delay(1600).then(function () {
        return api('/health', { timeout: 1600 });
      }).then(function () {
        S.helper = true;
        return true;
      }).catch(function () {
        S.helper = false;
        return false;
      });
    });
  }

  function ensureHelperDeep(tries) {
    return ensureHelper().then(function (ok) {
      if (ok) return true;
      if (tries <= 0) return false;
      render();
      return delay(1500).then(function () { return ensureHelperDeep(tries - 1); });
    });
  }

  function loadAll() {
    return api('/config', { timeout: 3000 }).then(function (d) {
      if (d && d.config) S.config = mergeConfig(DEFAULT_CONFIG, d.config);
    }).catch(function () {}).then(function () {
      return api('/state', { timeout: 3000 }).then(applyState).catch(function (e) {
        S.error = String(e.message || e);
      });
    });
  }

  function mergeConfig(base, got) {
    var out = clone(base);
    if (!got) return out;
    if (got.region) {
      out.region.x = num(got.region.x, out.region.x);
      out.region.y = num(got.region.y, out.region.y);
      out.region.w = num(got.region.w, out.region.w);
      out.region.h = num(got.region.h, out.region.h);
    }
    if (got.style) {
      out.style.size = num(got.style.size, out.style.size);
      out.style.bottom = num(got.style.bottom, out.style.bottom);
      out.style.opacity = num(got.style.opacity, out.style.opacity);
      out.style.color = typeof got.style.color === 'string' ? got.style.color : out.style.color;
    }
    if (got.ocr) {
      out.ocr.threshold = num(got.ocr.threshold, out.ocr.threshold);
      out.ocr.minConfidence = num(got.ocr.minConfidence, out.ocr.minConfidence);
    }
    return out;
  }

  function num(v, d) { return typeof v === 'number' && isFinite(v) ? v : d; }

  function applyState(st) {
    if (!st) return;
    if (typeof st.running === 'boolean') S.running = st.running;
    S.last = st.last || {};
    S.capture = st.capture || null;
    S.key = !!st.key;
    S.error = st.error || null;
    if (st.config) S.config = mergeConfig(S.config, st.config);
    render();
  }

  function startEngine() {
    if (S.busy) return;
    S.busy = true;
    S.error = null;
    render();
    ensureHelperDeep(2).then(function (ok) {
      if (!ok) throw new Error('Could not start the service. Is Vetro running on the TV?');
      return api('/start', { method: 'POST', body: {}, timeout: 8000 });
    }).then(function () {
      return delay(800);
    }).then(function () {
      return api('/state', { timeout: 3000 });
    }).then(function (st) {
      applyState(st);
      if (!S.running) S.error = 'The engine has not started yet; try again in a few seconds.';
    }).catch(function (e) {
      S.error = String(e.message || e);
    }).then(function () {
      S.busy = false;
      render();
    });
  }

  function stopEngine() {
    if (S.busy) return;
    S.busy = true;
    S.error = null;
    render();
    ensureHelperDeep(1).then(function (ok) {
      if (!ok) throw new Error('Service is offline.');
      return api('/stop', { method: 'POST', body: {}, timeout: 8000 });
    }).then(function () {
      S.running = false;
      return api('/state', { timeout: 2500 });
    }).then(applyState).catch(function (e) {
      S.error = String(e.message || e);
    }).then(function () {
      S.busy = false;
      render();
    });
  }

  var saveTimer = null;
  function saveConfig() {
    clearTimeout(saveTimer);
    saveTimer = setTimeout(function () {
      api('/config', { method: 'POST', body: { config: S.config }, timeout: 4000 }).catch(function (e) {
        S.error = String(e.message || e);
        render();
      });
    }, 300);
  }

  function normalizeRegion() {
    var r = S.config.region;
    r.w = clamp(r.w, 0.05, 1);
    r.h = clamp(r.h, 0.03, 0.8);
    r.x = clamp(r.x, 0, 1 - r.w);
    r.y = clamp(r.y, 0, 1 - r.h);
  }

  function adjust(key, dir) {
    var c = S.config;
    if (key === 'size') c.style.size = Math.round(clamp(c.style.size + dir * 2, 20, 64));
    else if (key === 'bottom') c.style.bottom = Math.round(clamp(c.style.bottom + dir * 20, 60, 1040));
    else if (key === 'opacity') c.style.opacity = clamp(round2(c.style.opacity + dir * 0.1), 0.2, 1);
    else if (key === 'rx') c.region.x = round3(clamp(c.region.x + dir * 0.01, 0, 1));
    else if (key === 'ry') c.region.y = round3(clamp(c.region.y + dir * 0.01, 0, 1));
    else if (key === 'rw') c.region.w = round3(clamp(c.region.w + dir * 0.01, 0.05, 1));
    else if (key === 'rh') c.region.h = round3(clamp(c.region.h + dir * 0.01, 0.03, 0.8));
    normalizeRegion();
    saveConfig();
    render();
  }

  function applyPreset(name) {
    if (name === 'bottom') S.config.region = { x: 0.208, y: 0.861, w: 0.583, h: 0.107 };
    if (name === 'middle') S.config.region = { x: 0.15, y: 0.44, w: 0.7, h: 0.16 };
    if (name === 'full') S.config.region = { x: 0.05, y: 0.08, w: 0.9, h: 0.84 };
    saveConfig();
    render();
  }

  function resetStyle() {
    S.config.style = { size: 34, bottom: 1000, opacity: 1, color: '#FFFFFF' };
    saveConfig();
    render();
  }

  function hexAlpha(opacity) {
    var a = Math.round(clamp(opacity, 0, 1) * 255).toString(16).toUpperCase();
    return a.length < 2 ? '0' + a : a;
  }

  // ---------------------------------------------------------------- rendering

  function render() {
    var c = S.config;

    chip('chip-service', S.helper ? 'on' : 'off', S.helper ? 'Service connected' : 'Service offline');
    chip('chip-engine', S.running ? 'on' : (S.busy ? 'wait' : 'off'), S.running ? 'Subtitles running' : (S.busy ? 'Starting' : 'Engine stopped'));
    chip('chip-key', S.key ? 'on' : 'off', S.key ? 'DeepL connected' : 'No DeepL key');

    $('hub-title').textContent = S.running ? 'Subtitles running' : (S.busy ? 'Starting…' : 'Subtitle engine stopped');
    $('hub-sub').textContent = S.error ? S.error : (S.running
      ? 'Reading the screen and translating English to Turkish. The subtitle band stays over the game; press OK to stop.'
      : 'Press Start. It reads the TV screen, translates the English subtitles to Turkish, and draws the subtitle band over the game.');
    $('hub-hint').textContent = S.running ? 'Remote: OK stop · Back exit' : 'Remote: OK start · Back exit';

    var btn = $('btn-toggle');
    btn.textContent = S.busy ? 'Please wait…' : (S.running ? 'Stop' : 'Start');
    btn.classList.toggle('running', S.running);
    btn.disabled = S.busy;

    $('foot-state').textContent = S.running ? 'Running · ' + (S.capture ? S.capture.w + '×' + S.capture.h : '')
      : (S.helper ? 'Ready' : 'Service offline');

    // values
    setVal('val-size', c.style.size);
    setVal('val-bottom', c.style.bottom);
    setVal('val-opacity', Math.round(c.style.opacity * 100) + '%');
    setVal('val-rx', Math.round(c.region.x * 100) + '%');
    setVal('val-ry', Math.round(c.region.y * 100) + '%');
    setVal('val-rw', Math.round(c.region.w * 100) + '%');
    setVal('val-rh', Math.round(c.region.h * 100) + '%');
    $('key-state').textContent = S.key ? 'Loaded' : 'Missing';

    // subtitle previews (1080p coordinates -> preview px)
    styleBand('hub-band', 0.37);
    styleBand('style-band', 0.34);

    // region preview
    var rect = $('region-rect');
    rect.style.left = (c.region.x * 100) + '%';
    rect.style.top = (c.region.y * 100) + '%';
    rect.style.width = (c.region.w * 100) + '%';
    rect.style.height = (c.region.h * 100) + '%';

    // diagnostics
    $('diag-run').textContent = S.running ? 'Running' : 'Stopped';
    $('diag-cap').textContent = S.capture ? S.capture.w + ' × ' + S.capture.h : '—';
    $('diag-ocr').textContent = S.last.ocr || '—';
    $('diag-tr').textContent = S.last.tr || '—';
    $('diag-ocrms').textContent = S.last.ocr_ms ? S.last.ocr_ms + ' ms' : '—';
    $('diag-trms').textContent = S.last.tr_ms ? S.last.tr_ms + ' ms' : '—';

    document.querySelectorAll('.seg').forEach(function (b) {
      b.classList.toggle('active', b.getAttribute('data-color') === c.style.color);
    });
  }

  function setVal(id, v) { var el = $(id); if (el) el.textContent = v; }

  function chip(id, state, label) {
    var el = $(id);
    if (!el) return;
    el.classList.remove('on', 'off', 'wait');
    el.classList.add(state);
    el.querySelector('span').textContent = label;
  }

  function styleBand(id, scale) {
    var el = $(id);
    if (!el) return;
    var c = S.config;
    el.style.fontSize = Math.round(c.style.size * scale) + 'px';
    el.style.bottom = 'auto';
    el.style.top = (c.style.bottom / 1080 * 100) + '%';
    el.style.transform = 'translate(-50%, -100%)';
    el.style.background = 'rgba(0,0,0,' + c.style.opacity + ')';
    el.style.color = c.style.color;
    el.style.width = '73%';
  }

  // ---------------------------------------------------------------- navigation

  function showView(name) {
    S.view = name;
    qsa('.view').forEach(function (v) { v.classList.toggle('active', v.id === 'view-' + name); });
    focusFirst();
  }

  function focusFirst() {
    var list = currentFocusables();
    setFocus(list.length ? list[0] : null);
  }

  function currentFocusables() {
    var view = document.querySelector('.view.active');
    if (!view) return [];
    return qsa('.focusable', view).filter(function (el) {
      return el.offsetParent !== null && !el.disabled;
    });
  }

  function setFocus(el) {
    qsa('.focus').forEach(function (f) { f.classList.remove('focus'); });
    if (el) {
      el.classList.add('focus');
      try { el.focus(); } catch (e) { /* ignore */ }
    }
  }

  function focusEl() { return document.querySelector('.focus'); }

  function move(dir) {
    var list = currentFocusables();
    var cur = focusEl();
    if (!list.length) return;
    if (!cur || list.indexOf(cur) === -1) { setFocus(list[0]); return; }
    var r = cur.getBoundingClientRect();
    var cx = r.left + r.width / 2;
    var cy = r.top + r.height / 2;
    var best = null, bestScore = 1e9;
    list.forEach(function (el) {
      if (el === cur) return;
      var q = el.getBoundingClientRect();
      var qx = q.left + q.width / 2;
      var qy = q.top + q.height / 2;
      var dx = qx - cx, dy = qy - cy;
      var ok = (dir === 'left' && dx < -4) || (dir === 'right' && dx > 4) ||
               (dir === 'up' && dy < -4) || (dir === 'down' && dy > 4);
      if (!ok) return;
      var primary = (dir === 'left' || dir === 'right') ? Math.abs(dx) : Math.abs(dy);
      var cross = (dir === 'left' || dir === 'right') ? Math.abs(dy) : Math.abs(dx);
      var score = primary + cross * 2.4;
      if (score < bestScore) { bestScore = score; best = el; }
    });
    if (best) setFocus(best);
  }

  function activate() {
    var el = focusEl();
    if (!el) return;
    if (el.id === 'btn-toggle') { S.running ? stopEngine() : startEngine(); return; }
    if (el.hasAttribute('data-view')) { showView(el.getAttribute('data-view')); return; }
    if (el.hasAttribute('data-action')) {
      var a = el.getAttribute('data-action');
      if (a === 'back') back();
      if (a === 'reset-style') resetStyle();
      return;
    }
    if (el.hasAttribute('data-preset')) { applyPreset(el.getAttribute('data-preset')); return; }
    if (el.hasAttribute('data-bottom')) { S.config.style.bottom = parseInt(el.getAttribute('data-bottom'), 10) || 1000; saveConfig(); render(); return; }
    if (el.hasAttribute('data-color')) { S.config.style.color = el.getAttribute('data-color'); saveConfig(); render(); return; }
    if (el.classList.contains('step')) {
      adjust(el.getAttribute('data-key'), parseInt(el.getAttribute('data-dir'), 10));
    }
  }

  function back() {
    if (S.view !== 'hub') { showView('hub'); return; }
    exitApp();
  }

  function exitApp() {
    try {
      if (window.tizen && tizen.application) {
        tizen.application.getCurrentApplication().exit();
        return;
      }
    } catch (e) { /* fall through */ }
    try { window.close(); } catch (e) { /* ignore */ }
  }

  function bindKeys() {
    document.addEventListener('keydown', function (e) {
      var k = e.keyCode;
      if (k === 10009 || k === 8 || k === 27 || k === 461) { e.preventDefault(); back(); return; }
      if (k === 13) { e.preventDefault(); activate(); return; }
      if (k === 37) { e.preventDefault(); move('left'); return; }
      if (k === 38) { e.preventDefault(); move('up'); return; }
      if (k === 39) { e.preventDefault(); move('right'); return; }
      if (k === 40) { e.preventDefault(); move('down'); return; }
    });
    document.addEventListener('click', function (e) {
      var el = e.target;
      while (el && el !== document.body && !el.classList.contains('focusable')) el = el.parentNode;
      if (el && el.classList && el.classList.contains('focusable')) { setFocus(el); activate(); }
    });
  }

  // ---------------------------------------------------------------- polling

  var pollTicks = 0;
  function poll() {
    pollTicks++;
    if (!S.helper) {
      if (pollTicks % 4 === 0) {
        ensureHelper().then(function (ok) { if (ok) loadAll(); });
      }
      return;
    }
    api('/state', { timeout: 2500 }).then(applyState).catch(function () {
      S.helper = false;
      S.running = false;
      render();
    });
  }

  function boot() {
    bindKeys();
    render();
    focusFirst();
    ensureHelperDeep(4).then(function (ok) {
      S.booting = false;
      $('app').classList.remove('booting');
      if (ok) return loadAll();
      render();
    });
    setInterval(poll, 1400);
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
  else boot();
})();
