/* ── Three.js — C2 node constellation ── */
(function () {
  const canvas = document.getElementById('bg-canvas');
  if (!canvas || typeof THREE === 'undefined') return;

  const mobile  = window.innerWidth < 768;
  const N       = mobile ? 50  : 160;
  const CONNECT = mobile ? 7.0 : 9.5;
  const MAXSEG  = mobile ? 220 : 1000;

  const renderer = new THREE.WebGLRenderer({ canvas, alpha: false, antialias: false });
  renderer.setPixelRatio(Math.min(devicePixelRatio, mobile ? 1 : 1.5));
  renderer.setClearColor(0x07080f, 1);

  const scene = new THREE.Scene();
  scene.fog = new THREE.FogExp2(0x07080f, mobile ? 0.044 : 0.030);

  const camera = new THREE.PerspectiveCamera(50, 1, 0.1, 55);
  camera.position.set(0, 0, 20);

  // ── Circular soft-glow sprite ─────────────────────────────────────────────
  function makeCircleSprite(coreAlpha, feather) {
    const sz = 64;
    const cv = document.createElement('canvas');
    cv.width = cv.height = sz;
    const ctx = cv.getContext('2d');
    const r = sz / 2;
    const g = ctx.createRadialGradient(r, r, 0, r, r, r);
    g.addColorStop(0,       'rgba(255,255,255,' + coreAlpha + ')');
    g.addColorStop(feather, 'rgba(255,255,255,0.35)');
    g.addColorStop(1,       'rgba(255,255,255,0)');
    ctx.fillStyle = g;
    ctx.fillRect(0, 0, sz, sz);
    const t = new THREE.CanvasTexture(cv);
    t.needsUpdate = true;
    return t;
  }

  const texNode = makeCircleSprite(0.90, 0.40);
  const texHub  = makeCircleSprite(1.00, 0.30);

  // ── Particle data ──────────────────────────────────────────────────────────
  const ptPos = new Float32Array(N * 3);
  const vel   = new Float32Array(N * 3);
  const BX = 22, BY = 13, BZ = 15;

  for (let i = 0; i < N; i++) {
    ptPos[i*3]   = (Math.random() - 0.5) * BX * 2;
    ptPos[i*3+1] = (Math.random() - 0.5) * BY * 2;
    ptPos[i*3+2] = (Math.random() - 0.5) * BZ * 2;
    vel[i*3]     = (Math.random() - 0.5) * 0.009;
    vel[i*3+1]   = (Math.random() - 0.5) * 0.007;
    vel[i*3+2]   = (Math.random() - 0.5) * 0.005;
  }

  // Regular nodes — small round dots
  const ptGeo = new THREE.BufferGeometry();
  ptGeo.setAttribute('position', new THREE.BufferAttribute(ptPos, 3));
  scene.add(new THREE.Points(ptGeo, new THREE.PointsMaterial({
    map: texNode,
    color: 0x3a70d4,
    size: mobile ? 0.40 : 0.30,
    sizeAttenuation: true,
    transparent: true,
    opacity: 0.65,
    depthWrite: false,
    alphaTest: 0.005,
  })));

  // Hub nodes — larger glowing circles
  const HUB_N  = Math.max(4, Math.floor(N * 0.09));
  const hubArr = [];
  const used   = new Set();
  while (hubArr.length < HUB_N) {
    const idx = Math.floor(Math.random() * N);
    if (!used.has(idx)) { used.add(idx); hubArr.push(idx); }
  }
  const hubPos = new Float32Array(HUB_N * 3);
  for (let h = 0; h < HUB_N; h++) {
    const i = hubArr[h];
    hubPos[h*3]   = ptPos[i*3];
    hubPos[h*3+1] = ptPos[i*3+1];
    hubPos[h*3+2] = ptPos[i*3+2];
  }
  const hubGeo = new THREE.BufferGeometry();
  hubGeo.setAttribute('position', new THREE.BufferAttribute(hubPos, 3));
  scene.add(new THREE.Points(hubGeo, new THREE.PointsMaterial({
    map: texHub,
    color: 0x5a9af8,
    size: mobile ? 0.70 : 0.58,
    sizeAttenuation: true,
    transparent: true,
    opacity: 0.90,
    depthWrite: false,
    alphaTest: 0.005,
  })));

  // ── Connection lines ───────────────────────────────────────────────────────
  const linePosArr = new Float32Array(MAXSEG * 6);
  const lineGeo = new THREE.BufferGeometry();
  lineGeo.setAttribute('position', new THREE.BufferAttribute(linePosArr, 3));
  const lineObj = new THREE.LineSegments(lineGeo, new THREE.LineBasicMaterial({
    color: 0x1a3870,
    transparent: true,
    opacity: mobile ? 0.22 : 0.18,
  }));
  scene.add(lineObj);

  // ── Resize ────────────────────────────────────────────────────────────────
  function resize() {
    renderer.setSize(window.innerWidth, window.innerHeight, false);
    camera.aspect = window.innerWidth / window.innerHeight;
    camera.updateProjectionMatrix();
  }
  window.addEventListener('resize', resize, { passive: true });
  resize();

  let paused = false;
  document.addEventListener('visibilitychange', () => {
    paused = document.hidden;
    if (!paused) { clock.start(); tick(); }
  });

  const clock = new THREE.Clock();
  let t = 0;

  function tick() {
    if (paused) return;
    requestAnimationFrame(tick);
    const dt = Math.min(clock.getDelta(), 0.05);
    t += dt;

    for (let i = 0; i < N; i++) {
      ptPos[i*3]   += vel[i*3]   * dt * 60;
      ptPos[i*3+1] += vel[i*3+1] * dt * 60;
      ptPos[i*3+2] += vel[i*3+2] * dt * 60;
      if (Math.abs(ptPos[i*3])   > BX) vel[i*3]   *= -1;
      if (Math.abs(ptPos[i*3+1]) > BY) vel[i*3+1] *= -1;
      if (Math.abs(ptPos[i*3+2]) > BZ) vel[i*3+2] *= -1;
    }
    ptGeo.attributes.position.needsUpdate = true;

    for (let h = 0; h < HUB_N; h++) {
      const i = hubArr[h];
      hubPos[h*3]   = ptPos[i*3];
      hubPos[h*3+1] = ptPos[i*3+1];
      hubPos[h*3+2] = ptPos[i*3+2];
    }
    hubGeo.attributes.position.needsUpdate = true;

    // Rebuild connection segments
    let seg = 0;
    const C2 = CONNECT * CONNECT;
    outer: for (let i = 0; i < N; i++) {
      for (let j = i + 1; j < N; j++) {
        const dx = ptPos[i*3]   - ptPos[j*3];
        const dy = ptPos[i*3+1] - ptPos[j*3+1];
        const dz = ptPos[i*3+2] - ptPos[j*3+2];
        if (dx*dx + dy*dy + dz*dz < C2) {
          linePosArr[seg*6]   = ptPos[i*3];
          linePosArr[seg*6+1] = ptPos[i*3+1];
          linePosArr[seg*6+2] = ptPos[i*3+2];
          linePosArr[seg*6+3] = ptPos[j*3];
          linePosArr[seg*6+4] = ptPos[j*3+1];
          linePosArr[seg*6+5] = ptPos[j*3+2];
          if (++seg >= MAXSEG) break outer;
        }
      }
    }
    lineGeo.attributes.position.needsUpdate = true;
    lineGeo.setDrawRange(0, seg * 2);

    // Gentle arc drift — never distracts from content
    if (!mobile) {
      camera.position.x = Math.sin(t * 0.048) * 2.5 + Math.sin(t * 0.019) * 0.7;
      camera.position.y = Math.sin(t * 0.033) * 1.2 + Math.sin(t * 0.015) * 0.4;
      camera.position.z = 20 + Math.sin(t * 0.022) * 1.4;
      camera.lookAt(Math.sin(t * 0.038) * 0.4, Math.sin(t * 0.026) * 0.2, 0);
    }

    renderer.render(scene, camera);
  }
  tick();
})();

/* ── Background music — starts on first interaction ── */
(function () {
  const audio = document.getElementById('bg-music');
  if (!audio) return;
  audio.volume = 0.28;
  function tryPlay() { audio.play().catch(() => {}); }
  tryPlay();
  document.addEventListener('click',      tryPlay, { once: true, passive: true });
  document.addEventListener('keydown',    tryPlay, { once: true, passive: true });
  document.addEventListener('touchstart', tryPlay, { once: true, passive: true });
})();

/* ── Nav scroll state ── */
window.addEventListener('scroll', () => {
  document.getElementById('nav').classList.toggle('scrolled', window.scrollY > 40);
}, { passive: true });

/* ── Scroll reveal ── */
const revealIO = new IntersectionObserver(entries => {
  entries.forEach(({ isIntersecting, target }) => {
    if (isIntersecting) { target.classList.add('visible'); revealIO.unobserve(target); }
  });
}, { threshold: 0.07 });
document.querySelectorAll('.reveal').forEach((el, i) => {
  el.style.transitionDelay = (i % 4) * 60 + 'ms';
  revealIO.observe(el);
});

/* ── 3D tilt on click/tap ── */
document.querySelectorAll('.gallery-item img, .screen-body img').forEach(img => {
  let busy = false;
  const box = img.closest('.gallery-item') || img.closest('.screen-frame') || img;
  function doTilt() {
    if (busy) return;
    busy = true;
    box.style.transition = 'transform 0.13s ease';
    box.style.transform  = 'perspective(700px) rotateY(18deg) scale(0.95)';
    setTimeout(() => {
      box.style.transform = 'perspective(700px) rotateY(-14deg) scale(0.95)';
      setTimeout(() => {
        box.style.transition = 'transform 0.22s ease';
        box.style.transform  = '';
        setTimeout(() => { busy = false; }, 220);
      }, 130);
    }, 130);
  }
  img.addEventListener('click', doTilt);
  img.addEventListener('touchend', e => { e.preventDefault(); doTilt(); }, { passive: false });
});

/* ── Smooth anchor scroll ── */
document.querySelectorAll('a[href^="#"]').forEach(a => {
  a.addEventListener('click', e => {
    const target = document.querySelector(a.getAttribute('href'));
    if (target) { e.preventDefault(); target.scrollIntoView({ behavior: 'smooth' }); }
  });
});
