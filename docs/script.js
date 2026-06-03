/* ── Three.js — radar grid ── */
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

  // ── Main grid — single solid color, wave-deformed ─────────────────────────
  const S1 = lowEnd ? 22 : mobile ? 34 : 66;
  const geo1 = new THREE.PlaneGeometry(50, 72, S1, S1);
  geo1.rotateX(-Math.PI / 2);
  const mesh1 = new THREE.Mesh(geo1, new THREE.MeshBasicMaterial({
    color: 0x112460, wireframe: true, transparent: true,
    opacity: mobile ? 0.40 : 0.46,
  }));
  mesh1.position.set(0, -1.2, -6);
  scene.add(mesh1);

  const pos1  = geo1.attributes.position;
  const base1 = new Float32Array(pos1.count);
  for (let i = 0; i < pos1.count; i++) base1[i] = pos1.getY(i);

  // ── Far sparse grid (parallax depth) ─────────────────────────────────────
  let farPos = null, farBase = null;
  if (!mobile) {
    const geo2 = new THREE.PlaneGeometry(80, 100, 22, 22);
    geo2.rotateX(-Math.PI / 2);
    const mesh2 = new THREE.Mesh(geo2, new THREE.MeshBasicMaterial({
      color: 0x091a40, wireframe: true, transparent: true, opacity: 0.12,
    }));
    mesh2.position.set(0, -4.0, -18);
    scene.add(mesh2);
    farPos  = geo2.attributes.position;
    farBase = new Float32Array(farPos.count);
    for (let i = 0; i < farPos.count; i++) farBase[i] = farPos.getY(i);
  }

  // ── Wave functions ────────────────────────────────────────────────────────
  function ambientWave(x, z, t) {
    return Math.sin(x * 0.28 + t * 0.78) * 0.48
         + Math.sin(z * 0.18 + t * 0.53) * 0.34
         + Math.sin((x - z) * 0.12 + t * 0.37) * 0.20
         + Math.sin((x + z) * 0.062 + t * 0.21) * 0.13
         + Math.sin(x * 0.055 + z * 0.042 + t * 0.14) * 0.09;
  }

  function sonarWave(x, z, t) {
    const d = Math.hypot(x, z);
    return Math.max(0, Math.sin(t * 2.1 - d * 0.27)) * Math.exp(-d * 0.058) * 1.1;
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
    if (!paused) tick();
  });

  let t = 0;
  function tick() {
    if (paused) return;
    requestAnimationFrame(tick);
    t += mobile ? 0.0016 : 0.0018;

    // Main grid wave
    for (let i = 0; i < pos1.count; i++)
      pos1.setY(i, base1[i] + ambientWave(pos1.getX(i), pos1.getZ(i), t)
                             + (mobile ? 0 : sonarWave(pos1.getX(i), pos1.getZ(i), t)));
    pos1.needsUpdate = true;

    // Far grid
    if (farPos && farBase) {
      for (let i = 0; i < farPos.count; i++)
        farPos.setY(i, farBase[i] + ambientWave(farPos.getX(i), farPos.getZ(i), t * 0.38) * 0.44);
      farPos.needsUpdate = true;
    }

    // Camera: tri-axis drift — X sway + Y breathe + Z slow forward/back
    camera.position.x = Math.sin(t * 0.110) * 0.50 + Math.sin(t * 0.037) * 0.14;
    camera.position.y = 4.2 + Math.sin(t * 0.072) * 0.20 + Math.sin(t * 0.041) * 0.08;
    camera.position.z = 8.0 + Math.sin(t * 0.022) * 1.8;
    camera.lookAt(Math.sin(t * 0.090) * 0.18, -0.5, -5);

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
