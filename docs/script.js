/* ── Three.js — lit wave surface ── */
(function () {
  const canvas = document.getElementById('bg-canvas');
  if (!canvas || typeof THREE === 'undefined') return;

  const mobile = window.innerWidth < 768;
  const lowEnd  = window.innerWidth < 480;

  const renderer = new THREE.WebGLRenderer({ canvas, alpha: false, antialias: !mobile });
  renderer.setPixelRatio(Math.min(devicePixelRatio, mobile ? 1 : 1.5));
  renderer.setClearColor(0x07080f, 1);

  const scene = new THREE.Scene();
  scene.fog = new THREE.FogExp2(0x07080f, mobile ? 0.026 : 0.017);

  const camera = new THREE.PerspectiveCamera(46, 1, 0.1, 80);
  camera.position.set(0, 3.8, 8.2);
  camera.lookAt(0, -0.8, -5);

  // ── Lights — subtle, match original dark-blue grid aesthetic ──────────────
  scene.add(new THREE.AmbientLight(0x101e38, 2.2));

  const light1 = new THREE.PointLight(0x4a85f5, mobile ? 1.4 : 2.0, 40);
  light1.position.set(4, 7, -3);
  scene.add(light1);

  const light2 = new THREE.PointLight(0x7c5ce8, mobile ? 0.9 : 1.4, 32);
  light2.position.set(-9, 5, -11);
  scene.add(light2);

  // ── Main surface — single matte color, same palette as original grid ───────
  const S1 = lowEnd ? 32 : mobile ? 52 : 96;
  const geo1 = new THREE.PlaneGeometry(56, 82, S1, S1);
  geo1.rotateX(-Math.PI / 2);
  const pos1  = geo1.attributes.position;
  const base1 = new Float32Array(pos1.count);
  for (let i = 0; i < pos1.count; i++) base1[i] = pos1.getY(i);

  const surface = new THREE.Mesh(geo1, new THREE.MeshStandardMaterial({
    color:    0x0e2855,
    emissive: 0x060f22,
    roughness: 0.65,
    metalness: 0.20,
    side: THREE.DoubleSide,
    transparent: true,
    opacity: 0.90,
  }));
  surface.position.set(0, -1.2, -6);
  scene.add(surface);

  // Far depth layer — faint, same palette, atmospheric recession
  let farData = null;
  if (!mobile) {
    const geoF = new THREE.PlaneGeometry(90, 120, 28, 28);
    geoF.rotateX(-Math.PI / 2);
    const posF  = geoF.attributes.position;
    const baseF = new Float32Array(posF.count);
    for (let i = 0; i < posF.count; i++) baseF[i] = posF.getY(i);
    const farSurf = new THREE.Mesh(geoF, new THREE.MeshStandardMaterial({
      color: 0x071a3a, emissive: 0x030c1a,
      roughness: 0.70, metalness: 0.12,
      side: THREE.DoubleSide, transparent: true, opacity: 0.32,
    }));
    farSurf.position.set(0, -3.8, -18);
    scene.add(farSurf);
    farData = { pos: posF, base: baseF, geo: geoF };
  }

  // ── Wave functions — broad swell + fine surface ripples ───────────────────
  function ambientWave(x, z, t) {
    const swell = Math.sin(x * 0.16 + t * 0.72) * 0.30
                + Math.sin(z * 0.11 + t * 0.50) * 0.22
                + Math.sin((x - z) * 0.072 + t * 0.35) * 0.14
                + Math.sin((x + z) * 0.036 + t * 0.19) * 0.09;
    const ripple = Math.sin(x * 0.46 + z * 0.34 + t * 1.10) * 0.045
                 + Math.sin(x * 0.62 - z * 0.44 + t * 0.88) * 0.032
                 + Math.sin(x * 0.31 + z * 0.55 + t * 1.38) * 0.022;
    return swell + ripple;
  }

  function sonarWave(x, z, t) {
    const d = Math.hypot(x, z);
    return Math.max(0, Math.sin(t * 1.9 - d * 0.22)) * Math.exp(-d * 0.048) * 0.65;
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

    // Main surface
    for (let i = 0; i < pos1.count; i++)
      pos1.setY(i, base1[i] + ambientWave(pos1.getX(i), pos1.getZ(i), t)
                             + (mobile ? 0 : sonarWave(pos1.getX(i), pos1.getZ(i), t)));
    pos1.needsUpdate = true;
    geo1.computeVertexNormals();

    // Far layer
    if (farData) {
      const { pos, base, geo } = farData;
      for (let i = 0; i < pos.count; i++)
        pos.setY(i, base[i] + ambientWave(pos.getX(i), pos.getZ(i), t * 0.36) * 0.38);
      pos.needsUpdate = true;
      geo.computeVertexNormals();
    }

    // Moving point lights
    light1.position.x = Math.sin(t * 0.085) * 14 + Math.sin(t * 0.031) * 4;
    light1.position.z = -6 + Math.cos(t * 0.062) * 10;
    light1.position.y = 7  + Math.sin(t * 0.100) * 1.5;
    light2.position.x = Math.sin(t * 0.058 + 1.9) * 16;
    light2.position.z = -6 + Math.cos(t * 0.044 + 0.8) * 12;
    light2.position.y = 5  + Math.sin(t * 0.076 + 1.2) * 2;

    // Camera
    camera.position.x = Math.sin(t * 0.110) * 0.50 + Math.sin(t * 0.037) * 0.14;
    camera.position.y = 3.8 + Math.sin(t * 0.068) * 0.18 + Math.sin(t * 0.039) * 0.07;
    camera.position.z = 8.2 + Math.sin(t * 0.018) * 1.1;
    camera.lookAt(Math.sin(t * 0.085) * 0.16, -0.8, -5);

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
