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
  const S1 = lowEnd ? 20 : mobile ? 30 : 60;
  const geo1 = new THREE.PlaneGeometry(50, 70, S1, S1);
  geo1.rotateX(-Math.PI / 2);
  const mesh1 = new THREE.Mesh(geo1, new THREE.MeshBasicMaterial({
    color: 0x0d3060, wireframe: true, transparent: true,
    opacity: mobile ? 0.36 : 0.40,
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
      color: 0x071a35, wireframe: true, transparent: true, opacity: 0.10,
    }));
    mesh2.position.set(0, -4.0, -18);
    scene.add(mesh2);
    farPos  = geo2.attributes.position;
    farBase = new Float32Array(farPos.count);
    for (let i = 0; i < farPos.count; i++) farBase[i] = farPos.getY(i);
  }

  // ── Radar ping — expanding LineLoop that pulses outward and fades ─────────
  const RING_N = mobile ? 56 : 92;
  const ringPts = [];
  for (let i = 0; i < RING_N; i++) {
    const a = (i / RING_N) * Math.PI * 2;
    ringPts.push(new THREE.Vector3(Math.cos(a), 0, Math.sin(a)));
  }
  const ringGeo  = new THREE.BufferGeometry().setFromPoints(ringPts);
  const ringMat  = new THREE.LineBasicMaterial({ color: 0x00ccff, transparent: true, opacity: 0 });
  const ringMat2 = new THREE.LineBasicMaterial({ color: 0x00ccff, transparent: true, opacity: 0 });
  const ring  = new THREE.LineLoop(ringGeo, ringMat);
  const ring2 = new THREE.LineLoop(ringGeo, ringMat2);
  ring.position.set(0, -1.18, -6);
  ring2.position.set(0, -1.19, -6);
  scene.add(ring, ring2);

  // ── Wave functions ────────────────────────────────────────────────────────
  function ambientWave(x, z, t) {
    return Math.sin(x * 0.28 + t * 0.78) * 0.42
         + Math.sin(z * 0.18 + t * 0.53) * 0.30
         + Math.sin((x - z) * 0.12 + t * 0.37) * 0.18
         + Math.sin((x + z) * 0.062 + t * 0.21) * 0.11;
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

  // Radar ring state
  const SONAR_T = 0.52; // ~5 s at 60 fps (t increments 0.0018/frame)
  const MAX_R   = 22;

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

    // Radar ring animation
    const sp  = (t % SONAR_T) / SONAR_T;
    const sp2 = Math.max(0, sp - 0.09);
    const fade = Math.pow(Math.max(0, 1 - sp * 1.05), 1.8);
    ring.scale.set(Math.max(0.01, sp * MAX_R), 1, Math.max(0.01, sp * MAX_R));
    ringMat.opacity = fade * 0.88;
    ring2.visible   = sp > 0.09;
    ring2.scale.set(Math.max(0.01, sp2 * MAX_R), 1, Math.max(0.01, sp2 * MAX_R));
    ringMat2.opacity = fade * 0.42;

    // Camera: dual-frequency sway
    camera.position.x = Math.sin(t * 0.110) * 0.45 + Math.sin(t * 0.037) * 0.12;
    camera.position.y = 4.2 + Math.sin(t * 0.072) * 0.18 + Math.sin(t * 0.041) * 0.07;
    camera.lookAt(Math.sin(t * 0.090) * 0.15, -0.5, -5);

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
