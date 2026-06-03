/* ── Three.js — wave grid ── */
(function () {
  const canvas = document.getElementById('bg-canvas');
  if (!canvas || typeof THREE === 'undefined') return;

  const mobile = window.innerWidth < 768;
  const lowEnd  = window.innerWidth < 480;

  const renderer = new THREE.WebGLRenderer({ canvas, alpha: false, antialias: !mobile });
  renderer.setPixelRatio(Math.min(devicePixelRatio, mobile ? 1 : 1.5));
  renderer.setClearColor(0x07080f, 1);

  const scene = new THREE.Scene();
  scene.fog = new THREE.FogExp2(0x07080f, mobile ? 0.028 : 0.020);

  const camera = new THREE.PerspectiveCamera(44, 1, 0.1, 80);
  camera.position.set(0, 4.2, 8.0);
  camera.lookAt(0, -0.5, -5);

  // ── Main grid ─────────────────────────────────────────────────────────────
  // 100 segments: cell size = 50/100 = 0.5 units
  // Wave spatial freq 0.14 → wavelength 44 units = 88 cells → perfectly round, zero spikes
  const S1 = lowEnd ? 28 : mobile ? 44 : 100;
  const geo1 = new THREE.PlaneGeometry(50, 70, S1, S1);
  geo1.rotateX(-Math.PI / 2);
  const mesh1 = new THREE.Mesh(geo1, new THREE.MeshBasicMaterial({
    color: 0x112460,
    wireframe: true,
    transparent: true,
    opacity: mobile ? 0.36 : 0.42,
  }));
  mesh1.position.set(0, -1.2, -6);
  scene.add(mesh1);

  const pos1  = geo1.attributes.position;
  const base1 = new Float32Array(pos1.count);
  for (let i = 0; i < pos1.count; i++) base1[i] = pos1.getY(i);

  // ── Far parallax grid ─────────────────────────────────────────────────────
  let farPos = null, farBase = null;
  if (!mobile) {
    const geo2 = new THREE.PlaneGeometry(80, 100, 24, 24);
    geo2.rotateX(-Math.PI / 2);
    const mesh2 = new THREE.Mesh(geo2, new THREE.MeshBasicMaterial({
      color: 0x091a40, wireframe: true, transparent: true, opacity: 0.10,
    }));
    mesh2.position.set(0, -4.0, -18);
    scene.add(mesh2);
    farPos  = geo2.attributes.position;
    farBase = new Float32Array(farPos.count);
    for (let i = 0; i < farPos.count; i++) farBase[i] = farPos.getY(i);
  }

  // ── Wave functions ─────────────────────────────────────────────────────────
  // Low spatial frequencies (0.10–0.14) → wavelengths 44–63 units
  // At 100 segs (cell=0.5u): each wave covers 88–126 cells → zero spike effect
  function ambientWave(x, z, t) {
    return Math.sin(x * 0.14 + t * 0.72) * 0.38
         + Math.sin(z * 0.10 + t * 0.50) * 0.28
         + Math.sin((x - z) * 0.068 + t * 0.35) * 0.18
         + Math.sin((x + z) * 0.034 + t * 0.20) * 0.12
         + Math.sin(x * 0.048 + z * 0.036 + t * 0.14) * 0.08;
  }

  function sonarWave(x, z, t) {
    const d = Math.hypot(x, z);
    return Math.max(0, Math.sin(t * 1.9 - d * 0.20)) * Math.exp(-d * 0.048) * 0.90;
  }

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
  const SPEED = mobile ? 0.130 : 0.150;

  let t = 0;
  function tick() {
    if (paused) return;
    requestAnimationFrame(tick);

    const dt = Math.min(clock.getDelta(), 0.05);
    t += dt * SPEED;

    for (let i = 0; i < pos1.count; i++)
      pos1.setY(i, base1[i] + ambientWave(pos1.getX(i), pos1.getZ(i), t)
                             + (mobile ? 0 : sonarWave(pos1.getX(i), pos1.getZ(i), t)));
    pos1.needsUpdate = true;

    if (farPos && farBase) {
      for (let i = 0; i < farPos.count; i++)
        farPos.setY(i, farBase[i] + ambientWave(farPos.getX(i), farPos.getZ(i), t * 0.38) * 0.44);
      farPos.needsUpdate = true;
    }

    camera.position.x = Math.sin(t * 0.110) * 0.48 + Math.sin(t * 0.037) * 0.13;
    camera.position.y = 4.2 + Math.sin(t * 0.068) * 0.18 + Math.sin(t * 0.039) * 0.07;
    camera.position.z = 8.0 + Math.sin(t * 0.018) * 1.1;
    camera.lookAt(Math.sin(t * 0.085) * 0.16, -0.5, -5);

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
