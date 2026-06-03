/* ── Three.js — ambient wave terrain ── */
(function () {
  const canvas = document.getElementById('bg-canvas');
  if (!canvas || typeof THREE === 'undefined') return;

  const mobile = window.innerWidth < 768;
  const lowEnd = mobile && window.innerWidth < 480;

  const renderer = new THREE.WebGLRenderer({ canvas, alpha: false, antialias: false });
  renderer.setPixelRatio(Math.min(devicePixelRatio, mobile ? 1 : 1.5));
  renderer.setClearColor(0x07080f, 1);

  const scene = new THREE.Scene();
  // Tight linear fog — grid dissolves cleanly at horizon, no circular artifact
  scene.fog = new THREE.Fog(0x07080f, mobile ? 10 : 15, mobile ? 38 : 50);

  const camera = new THREE.PerspectiveCamera(46, 1, 0.1, 80);
  camera.position.set(0, 5, 8);
  camera.lookAt(0, 0, -4);

  // ── Grid geometry ────────────────────────────────────────────────────────
  // Single grid on mobile (GPU friendly), dual grid on desktop (depth)
  const S1 = lowEnd ? 18 : mobile ? 24 : 48;
  const geo1 = new THREE.PlaneGeometry(46, 60, S1, S1);
  geo1.rotateX(-Math.PI / 2);
  const mesh1 = new THREE.Mesh(geo1, new THREE.MeshBasicMaterial({
    color: 0x0f2438,
    wireframe: true,
    transparent: true,
    opacity: mobile ? 0.13 : 0.16,
  }));
  mesh1.position.set(0, -1.5, -6);
  scene.add(mesh1);

  // Layer 2: desktop only — sparser, further back, slower wave = parallax
  let geo2 = null, pos2 = null, base2 = null;
  if (!mobile) {
    geo2 = new THREE.PlaneGeometry(66, 76, 18, 18);
    geo2.rotateX(-Math.PI / 2);
    const mesh2 = new THREE.Mesh(geo2, new THREE.MeshBasicMaterial({
      color: 0x091820, wireframe: true, transparent: true, opacity: 0.07,
    }));
    mesh2.position.set(0, -3, -15);
    scene.add(mesh2);
    pos2 = geo2.attributes.position;
    base2 = new Float32Array(pos2.count);
    for (let i = 0; i < pos2.count; i++) base2[i] = pos2.getY(i);
  }

  const pos1 = geo1.attributes.position;
  const base1 = new Float32Array(pos1.count);
  for (let i = 0; i < pos1.count; i++) base1[i] = pos1.getY(i);

  // 4-frequency wave — overlapping sines create pseudo-noise (no obvious repeat)
  // Amplitudes small so the terrain feels flat and ambient, not dramatic
  function wave(x, z, t) {
    return Math.sin(x * 0.28 + t * 0.65) * 0.28
         + Math.sin(z * 0.17 + t * 0.44) * 0.22
         + Math.sin((x - z) * 0.11 + t * 0.30) * 0.13
         + Math.sin((x + z) * 0.055 + t * 0.18) * 0.08;
  }

  function resize() {
    const w = window.innerWidth, h = window.innerHeight;
    renderer.setSize(w, h, false);
    camera.aspect = w / h;
    camera.updateProjectionMatrix();
  }
  window.addEventListener('resize', resize, { passive: true });
  resize();

  // Pause on hidden tab — battery/GPU saving
  let paused = false;
  document.addEventListener('visibilitychange', () => {
    paused = document.hidden;
    if (!paused) tick();
  });

  let t = 0;
  function tick() {
    if (paused) return;
    requestAnimationFrame(tick);

    // Slow speed = ambient / background feel, not foreground animation
    t += mobile ? 0.0014 : 0.0010;

    for (let i = 0; i < pos1.count; i++)
      pos1.setY(i, base1[i] + wave(pos1.getX(i), pos1.getZ(i), t));
    pos1.needsUpdate = true;

    if (pos2 && base2) {
      // Half speed + half amplitude → visually receding layer
      for (let i = 0; i < pos2.count; i++)
        pos2.setY(i, base2[i] + wave(pos2.getX(i), pos2.getZ(i), t * 0.4) * 0.45);
      pos2.needsUpdate = true;
    }

    // Very slow, very small camera sway — imperceptible without comparison
    camera.position.x = Math.sin(t * 0.10) * 0.35;
    camera.lookAt(0, 0, -4);

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
    box.style.transform = 'perspective(700px) rotateY(18deg) scale(0.95)';
    setTimeout(() => {
      box.style.transform = 'perspective(700px) rotateY(-14deg) scale(0.95)';
      setTimeout(() => {
        box.style.transition = 'transform 0.22s ease';
        box.style.transform = '';
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
    const t = document.querySelector(a.getAttribute('href'));
    if (t) { e.preventDefault(); t.scrollIntoView({ behavior: 'smooth' }); }
  });
});
