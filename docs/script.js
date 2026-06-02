/* ── Three.js — dual wave grid ── */
(function () {
  const canvas = document.getElementById('bg-canvas');
  if (!canvas || typeof THREE === 'undefined') return;

  const mobile = window.innerWidth < 640;

  const renderer = new THREE.WebGLRenderer({ canvas, alpha: false, antialias: false });
  renderer.setPixelRatio(Math.min(devicePixelRatio, 1.5));
  renderer.setClearColor(0x07080f, 1);

  const scene = new THREE.Scene();
  // Linear fog — no circular halo (FogExp2 causes that artifact)
  scene.fog = new THREE.Fog(0x07080f, 18, 55);

  const camera = new THREE.PerspectiveCamera(50, 1, 0.1, 80);
  camera.position.set(0, 4, 7);
  camera.lookAt(0, 0, -5);

  // Layer 1: dense front grid
  const S1 = mobile ? 28 : 52;
  const geo1 = new THREE.PlaneGeometry(50, 65, S1, S1);
  geo1.rotateX(-Math.PI / 2);
  const mesh1 = new THREE.Mesh(geo1, new THREE.MeshBasicMaterial({
    color: 0x162e52, wireframe: true, transparent: true, opacity: 0.21,
  }));
  mesh1.position.set(0, -1.5, -7);
  scene.add(mesh1);

  // Layer 2: sparse background grid — adds depth via parallax
  const S2 = mobile ? 14 : 22;
  const geo2 = new THREE.PlaneGeometry(70, 80, S2, S2);
  geo2.rotateX(-Math.PI / 2);
  const mesh2 = new THREE.Mesh(geo2, new THREE.MeshBasicMaterial({
    color: 0x0d1e38, wireframe: true, transparent: true, opacity: 0.10,
  }));
  mesh2.position.set(0, -3, -16);
  scene.add(mesh2);

  const pos1 = geo1.attributes.position;
  const pos2 = geo2.attributes.position;
  const base1 = new Float32Array(pos1.count);
  const base2 = new Float32Array(pos2.count);
  for (let i = 0; i < pos1.count; i++) base1[i] = pos1.getY(i);
  for (let i = 0; i < pos2.count; i++) base2[i] = pos2.getY(i);

  // 4-frequency wave — looks organic, avoids obvious sine repetition
  function wave(x, z, t) {
    return Math.sin(x * 0.32 + t * 0.8)  * 0.32
         + Math.sin(z * 0.20 + t * 0.55) * 0.26
         + Math.sin((x - z) * 0.14 + t * 0.38) * 0.16
         + Math.sin((x + z) * 0.07 + t * 0.22) * 0.10;
  }

  function resize() {
    const w = window.innerWidth, h = window.innerHeight;
    renderer.setSize(w, h, false);
    camera.aspect = w / h;
    camera.updateProjectionMatrix();
  }
  window.addEventListener('resize', resize, { passive: true });
  resize();

  // Pause rendering when tab is hidden — saves battery/GPU
  let paused = false;
  document.addEventListener('visibilitychange', () => {
    paused = document.hidden;
    if (!paused) tick();
  });

  let t = 0;
  function tick() {
    if (paused) return;
    requestAnimationFrame(tick);
    t += 0.002;

    for (let i = 0; i < pos1.count; i++)
      pos1.setY(i, base1[i] + wave(pos1.getX(i), pos1.getZ(i), t));
    pos1.needsUpdate = true;

    // Layer 2 at half speed for parallax
    for (let i = 0; i < pos2.count; i++)
      pos2.setY(i, base2[i] + wave(pos2.getX(i), pos2.getZ(i), t * 0.5) * 0.55);
    pos2.needsUpdate = true;

    // Imperceptible camera drift — adds life
    camera.position.x = Math.sin(t * 0.14) * 0.5;
    camera.lookAt(0, 0, -5);

    renderer.render(scene, camera);
  }
  tick();
})();

/* ── Background music — starts on first click/key/touch ── */
(function () {
  const audio = document.getElementById('bg-music');
  if (!audio) return;
  audio.volume = 0.28;

  function tryPlay() {
    audio.play().catch(() => {});
  }

  // Scroll does NOT count as a trusted user gesture for audio — browsers block it.
  // Only click, keydown, touchstart trigger autoplay unlock.
  tryPlay(); // works if browser allows autoplay (e.g. user has interacted before)
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

/* ── Smooth anchor scroll ── */
document.querySelectorAll('a[href^="#"]').forEach(a => {
  a.addEventListener('click', e => {
    const t = document.querySelector(a.getAttribute('href'));
    if (t) { e.preventDefault(); t.scrollIntoView({ behavior: 'smooth' }); }
  });
});
