/* ── Three.js — C2 global network globe ── */
(function () {
  const canvas = document.getElementById('bg-canvas');
  if (!canvas || typeof THREE === 'undefined') return;

  const mobile = window.innerWidth < 768;
  const R      = 10;   // globe radius

  const renderer = new THREE.WebGLRenderer({ canvas, alpha: false, antialias: true });
  renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
  renderer.setClearColor(0x07080f, 1);

  const scene = new THREE.Scene();
  scene.fog = new THREE.FogExp2(0x07080f, mobile ? 0.026 : 0.018);

  const camera = new THREE.PerspectiveCamera(44, 1, 0.1, 80);
  camera.position.set(0, 2, 22);
  camera.lookAt(0, 0, 0);

  // ── Atmospheric glow behind globe ──────────────────────────────────────
  (function () {
    const sz = 512;
    const cv = document.createElement('canvas');
    cv.width = cv.height = sz;
    const ctx = cv.getContext('2d');
    const g = ctx.createRadialGradient(sz/2,sz/2,0, sz/2,sz/2,sz/2);
    g.addColorStop(0,   'rgba(20,70,200,0.13)');
    g.addColorStop(0.35,'rgba(15,55,180,0.06)');
    g.addColorStop(1,   'rgba(0,0,0,0)');
    ctx.fillStyle = g; ctx.fillRect(0, 0, sz, sz);
    const spr = new THREE.Sprite(new THREE.SpriteMaterial({
      map: new THREE.CanvasTexture(cv),
      transparent: true, depthWrite: false,
      blending: THREE.AdditiveBlending,
    }));
    spr.scale.set(30, 30, 1);
    scene.add(spr);
  })();

  // ── Globe lat/lon wireframe ────────────────────────────────────────────
  const globeGroup = new THREE.Group();
  scene.add(globeGroup);

  const LAT = mobile ? 9  : 15;   // latitude rings
  const LON = mobile ? 14 : 26;   // meridian arcs
  const SEG = mobile ? 48 : 80;   // smoothness per line

  const latMat = new THREE.LineBasicMaterial({
    color: 0x0d2560, transparent: true, opacity: mobile ? 0.50 : 0.38,
  });
  const lonMat = new THREE.LineBasicMaterial({
    color: 0x0f2870, transparent: true, opacity: mobile ? 0.38 : 0.28,
  });

  // Latitude circles (horizontal rings)
  for (let i = 1; i < LAT; i++) {
    const phi = (i / LAT) * Math.PI;
    const ry  = R * Math.cos(phi);
    const rr  = R * Math.sin(phi);
    const pts = [];
    for (let j = 0; j <= SEG; j++) {
      const th = (j / SEG) * Math.PI * 2;
      pts.push(new THREE.Vector3(rr * Math.cos(th), ry, rr * Math.sin(th)));
    }
    globeGroup.add(new THREE.Line(new THREE.BufferGeometry().setFromPoints(pts), latMat));
  }

  // Longitude arcs (meridians — pole to pole)
  for (let i = 0; i < LON; i++) {
    const th  = (i / LON) * Math.PI * 2;
    const pts = [];
    for (let j = 0; j <= SEG; j++) {
      const phi = (j / SEG) * Math.PI;
      pts.push(new THREE.Vector3(
        R * Math.sin(phi) * Math.cos(th),
        R * Math.cos(phi),
        R * Math.sin(phi) * Math.sin(th)
      ));
    }
    globeGroup.add(new THREE.Line(new THREE.BufferGeometry().setFromPoints(pts), lonMat));
  }

  // ── Equator highlight ─────────────────────────────────────────────────
  (function () {
    const pts = [];
    for (let j = 0; j <= 120; j++) {
      const th = (j / 120) * Math.PI * 2;
      pts.push(new THREE.Vector3(R * Math.cos(th), 0, R * Math.sin(th)));
    }
    globeGroup.add(new THREE.Line(
      new THREE.BufferGeometry().setFromPoints(pts),
      new THREE.LineBasicMaterial({ color: 0x1a3a80, transparent: true, opacity: mobile ? 0.65 : 0.55 })
    ));
  })();

  // ── Connection arcs (Bezier curves between globe surface points) ───────
  const ARC_N = mobile ? 4 : 7;
  const arcs  = [];

  function randomSurfacePoint() {
    const phi = Math.acos(2 * Math.random() - 1);
    const th  = Math.random() * Math.PI * 2;
    return new THREE.Vector3(
      R * Math.sin(phi) * Math.cos(th),
      R * Math.cos(phi),
      R * Math.sin(phi) * Math.sin(th)
    );
  }

  function spawnArc(delay) {
    const a    = randomSurfacePoint();
    const b    = randomSurfacePoint();
    const ctrl = a.clone().add(b).multiplyScalar(0.5).normalize().multiplyScalar(R * 1.6);
    const pts  = new THREE.QuadraticBezierCurve3(a, ctrl, b).getPoints(56);
    const geo  = new THREE.BufferGeometry().setFromPoints(pts);
    const mat  = new THREE.LineBasicMaterial({ color: 0x2468e8, transparent: true, opacity: 0 });
    const line = new THREE.Line(geo, mat);
    globeGroup.add(line);
    return { line, mat, t: -(delay || 0), dur: 3.5 + Math.random() * 3, peak: 0.50 + Math.random() * 0.30 };
  }

  for (let i = 0; i < ARC_N; i++) arcs.push(spawnArc((i / ARC_N) * 5));

  // ── Resize — debounced to avoid scroll-bar jank on mobile ────────────
  let resizeTimer;
  function resize() {
    const w = window.innerWidth, h = window.innerHeight;
    renderer.setSize(w, h, false);
    camera.aspect = w / h;
    camera.updateProjectionMatrix();
  }
  window.addEventListener('resize', () => {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(resize, 120);
  }, { passive: true });
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

    // Globe slow rotation + subtle tilt oscillation
    globeGroup.rotation.y = t * 0.048;
    globeGroup.rotation.x = Math.sin(t * 0.016) * 0.07;
    globeGroup.rotation.z = Math.sin(t * 0.011) * 0.03;

    // Arc lifecycle
    for (let i = 0; i < arcs.length; i++) {
      const a = arcs[i];
      a.t += dt;
      const p = a.t / a.dur;
      if (p < 0) { a.mat.opacity = 0; continue; }
      if (p >= 1) {
        globeGroup.remove(a.line);
        a.line.geometry.dispose(); a.mat.dispose();
        arcs[i] = spawnArc(0);
        continue;
      }
      // fade in 0-25%, hold 25-70%, fade out 70-100%
      if (p < 0.25)      a.mat.opacity = a.peak * (p / 0.25);
      else if (p < 0.70) a.mat.opacity = a.peak;
      else               a.mat.opacity = a.peak * (1 - (p - 0.70) / 0.30);
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

/* ── Lightbox — gallery images open full-screen ── */
(function () {
  const lb    = document.getElementById('lightbox');
  const lbImg = document.getElementById('lightbox-img');
  const lbBtn = document.getElementById('lightbox-close');
  if (!lb || !lbImg) return;

  function open(src, alt) {
    lbImg.src = src; lbImg.alt = alt || '';
    lb.classList.add('open');
    document.body.style.overflow = 'hidden';
  }
  function close() {
    lb.classList.remove('open');
    document.body.style.overflow = '';
    setTimeout(() => { lbImg.src = ''; }, 200);
  }

  document.querySelectorAll('.gallery-item img').forEach(img => {
    img.style.cursor = 'zoom-in';
    img.addEventListener('click', () => open(img.src, img.alt));
    img.addEventListener('touchend', e => { e.preventDefault(); open(img.src, img.alt); }, { passive: false });
  });

  lb.addEventListener('click', e => { if (e.target === lb || e.target === lbBtn) close(); });
  document.addEventListener('keydown', e => { if (e.key === 'Escape') close(); });
})();

/* ── 3D tilt on hero dashboard click only ── */
document.querySelectorAll('.screen-body img').forEach(img => {
  let busy = false;
  const box = img.closest('.screen-frame') || img;
  function doTilt() {
    if (busy) return; busy = true;
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
