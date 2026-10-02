(() => {
  'use strict';
  const $ = (id) => document.getElementById(id);
  const video = $('video'), overlay = $('overlay'), playerEl = $('player');
  const errorEl = $('error'), errorText = $('error-text'), badge = $('badge');
  const listEl = $('channels'), listStatus = $('list-status');

  let hls = null, current = null, tuneTimer = null, stallTimer = null, events = null, evRetry = null, evDelay = 2000, session = 0;

  const BADGES = {
    Program: ['program', 'Program'],
    Commercial: ['commercial', 'Commercial'],
    Transition: ['transition', 'Transition'],
    Unknown: ['unknown', 'Unknown'],
  };
  const ERR = {
    503: 'Both tuners are busy. Close another stream or try again shortly.',
    504: 'The channel took too long to start. It may be off air.',
    404: 'This channel is offline or no longer available.',
    502: 'The tuner is unreachable. Check that it is powered on and on the network.',
  };

  function showOverlay(text) { overlay.textContent = text; overlay.hidden = !text; }
  function showError(text) { clearTimers(); showOverlay(''); errorText.textContent = text; errorEl.hidden = false; }
  function clearTimers() { clearInterval(tuneTimer); clearTimeout(stallTimer); tuneTimer = stallTimer = null; }

  async function loadChannels() {
    listStatus.hidden = false; listStatus.textContent = 'Loading channels…'; $('reload').hidden = true;
    try {
      const res = await fetch('/api/channels', { cache: 'no-store' });
      if (!res.ok) throw new Error(String(res.status));
      const chans = await res.json();
      listEl.replaceChildren();
      if (!chans.length) { listStatus.textContent = 'No channels found.'; $('reload').hidden = false; return; }
      listStatus.hidden = true;
      for (const c of chans) {
        const li = document.createElement('li');
        const b = document.createElement('button');
        b.type = 'button'; b.className = 'ch'; b.dataset.gn = c.guideNumber;
        b.innerHTML = '<span class="num"></span><span class="nm"></span><span class="cd"></span>';
        b.children[0].textContent = c.guideNumber;
        b.children[1].textContent = c.name;
        b.children[2].textContent = c.videoCodec || '';
        b.addEventListener('click', () => play(c));
        li.appendChild(b); listEl.appendChild(li);
      }
    } catch (e) {
      listStatus.textContent = 'Could not load channels. Check your connection to the relay.';
      $('reload').hidden = false;
    }
  }

  function markCurrent() {
    for (const b of listEl.querySelectorAll('.ch')) {
      if (current && b.dataset.gn === current.guideNumber) b.setAttribute('aria-current', 'true');
      else b.removeAttribute('aria-current');
    }
  }

  function teardown() {
    session++;
    clearTimers(); stopEvents();
    if (hls) { hls.destroy(); hls = null; }
    video.pause(); video.removeAttribute('src'); video.load(); // releases the HLS request so the idle reaper frees the tuner
  }

  async function play(chan) {
    teardown();
    const my = session;
    current = chan; markCurrent();
    playerEl.hidden = false; errorEl.hidden = true; badge.hidden = true;
    $('now-name').textContent = chan.guideNumber + ' ' + chan.name;
    playerEl.scrollIntoView({ behavior: 'smooth', block: 'start' });

    const url = '/hls/' + encodeURIComponent(chan.guideNumber) + '/index.m3u8';
    const t0 = Date.now();
    const tick = () => showOverlay('Tuning… ' + Math.floor((Date.now() - t0) / 1000) + 's (can take up to 45s)');
    tick(); tuneTimer = setInterval(tick, 1000);

    // Probe first so we can show a precise status for 503/404/502/504 (hls.js only reports a generic error).
    try {
      const res = await fetch(url, { cache: 'no-store' });
      if (my !== session) return;
      if (!res.ok) { showError(ERR[res.status] || 'Playback failed (HTTP ' + res.status + ').'); return; }
    } catch (e) {
      if (my !== session) return;
      showError('Could not reach the relay. Check your network and retry.'); return;
    }

    startPlayback(url, my);
    startEvents(chan.guideNumber);
  }

  function armStall(my) {
    clearTimeout(stallTimer);
    stallTimer = setTimeout(() => { if (my === session) showError('The stream stalled.'); }, 20000);
  }

  function startPlayback(url, my) {
    const onPlaying = () => { clearTimers(); showOverlay(''); errorEl.hidden = true; };
    video.onplaying = onPlaying;
    video.onwaiting = () => { if (my === session && !tuneTimer) { showOverlay('Buffering…'); armStall(my); } };
    video.onstalled = () => { if (my === session && !tuneTimer) armStall(my); };
    video.onerror = () => { if (my === session) showError('Playback error. The stream may have ended.'); };
    if (video.canPlayType('application/vnd.apple.mpegurl')) {
      video.src = url;
    } else if (window.Hls && Hls.isSupported()) {
      hls = new Hls({ lowLatencyMode: false });
      hls.on(Hls.Events.ERROR, (_e, d) => {
        if (my !== session || !d.fatal) return;
        const code = d.response && d.response.code;
        showError(ERR[code] || 'Playback failed (' + d.details + ').');
      });
      hls.loadSource(url); hls.attachMedia(video);
    } else { showError('This browser cannot play HLS.'); return; }
    armStall(my);
    const p = video.play(); if (p && p.catch) p.catch(() => showOverlay('Tap the video to start playback.'));
  }

  function stopEvents() { if (events) { events.close(); events = null; } clearTimeout(evRetry); evRetry = null; evDelay = 2000; }

  function startEvents(gn) {
    if (!window.EventSource) return;
    const my = session;
    const open = () => {
      if (my !== session) return;
      events = new EventSource('/api/channels/' + encodeURIComponent(gn) + '/events');
      events.onopen = () => { evDelay = 2000; };
      events.onmessage = (m) => {
        try {
          const d = JSON.parse(m.data); const b = BADGES[d.state] || BADGES.Unknown;
          badge.className = 'badge ' + b[0];
          badge.textContent = b[1] + (typeof d.confidence === 'number' ? ' ' + Math.round(d.confidence * 100) + '%' : '');
          badge.hidden = false;
        } catch (_) { /* ignore malformed event */ }
      };
      events.onerror = () => {
        // Endpoint absent (404) or dropped: hide badge and back off exponentially (max 60s) instead of spamming.
        events.close(); events = null; badge.hidden = true;
        if (my !== session) return;
        evRetry = setTimeout(open, evDelay); evDelay = Math.min(evDelay * 2, 60000);
      };
    };
    open();
  }

  function stop() {
    teardown(); current = null; markCurrent();
    playerEl.hidden = true; errorEl.hidden = true; showOverlay('');
  }

  $('stop').addEventListener('click', stop);
  $('retry').addEventListener('click', () => { if (current) play(current); });
  $('reload').addEventListener('click', loadChannels);
  window.addEventListener('pagehide', teardown);

  if ('serviceWorker' in navigator) {
    window.addEventListener('load', () => navigator.serviceWorker.register('/sw.js').catch(() => {}));
  }
  loadChannels();
})();
