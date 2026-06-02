/* ── Three.js — perspective wave grid ── */
(function () {
  const canvas = document.getElementById('bg-canvas');
  if (!canvas || typeof THREE === 'undefined') return;

  const renderer = new THREE.WebGLRenderer({ canvas, alpha: true, antialias: false });
  renderer.setPixelRatio(Math.min(devicePixelRatio, 1.5));
  renderer.setClearColor(0x000000, 0);

  const scene = new THREE.Scene();
  // Fog matching background — makes the grid dissolve at horizon
  scene.fog = new THREE.FogExp2(0x07080f, 0.055);

  const camera = new THREE.PerspectiveCamera(52, 1, 0.1, 80);
  camera.position.set(0, 5, 8);
  camera.lookAt(0, 0, -6);

  // Fewer segments on mobile to save GPU
  const SEGS = window.innerWidth < 640 ? 32 : 60;
  const geo = new THREE.PlaneGeometry(60, 70, SEGS, SEGS);
  geo.rotateX(-Math.PI / 2);

  const mat = new THREE.MeshBasicMaterial({
    color: 0x1a3560,
    wireframe: true,
    transparent: true,
    opacity: 0.22,
  });

  const mesh = new THREE.Mesh(geo, mat);
  mesh.position.set(0, -1, -8);
  scene.add(mesh);

  // Cache original Y positions (all 0 for a flat plane after rotateX)
  const pos = geo.attributes.position;
  const baseY = new Float32Array(pos.count);
  for (let i = 0; i < pos.count; i++) baseY[i] = pos.getY(i);

  function resize() {
    const w = window.innerWidth, h = window.innerHeight;
    renderer.setSize(w, h, false);
    camera.aspect = w / h;
    camera.updateProjectionMatrix();
  }
  window.addEventListener('resize', resize, { passive: true });
  resize();

  let t = 0;
  (function tick() {
    requestAnimationFrame(tick);
    t += 0.0025;

    // Displace vertices: two overlapping sine waves at different frequencies
    for (let i = 0; i < pos.count; i++) {
      const x = pos.getX(i);
      const z = pos.getZ(i);
      const y = baseY[i]
        + Math.sin(x * 0.28 + t) * 0.38
        + Math.sin(z * 0.18 + t * 0.65) * 0.28
        + Math.sin((x + z) * 0.12 + t * 0.4) * 0.18;
      pos.setY(i, y);
    }
    pos.needsUpdate = true;

    // Very gentle camera sway — not noticed consciously, just adds life
    camera.position.x = Math.sin(t * 0.18) * 0.6;
    camera.lookAt(0, 0, -6);

    renderer.render(scene, camera);
  })();
})();

/* ── Background music — no UI, starts on first interaction ── */
(function () {
  const audio = document.getElementById('bg-music');
  if (!audio) return;
  audio.volume = 0.28;

  // Try immediate autoplay (works in some browsers/contexts)
  audio.play().catch(() => {
    // Blocked — unlock silently on first user gesture
    const unlock = () => {
      audio.play().catch(() => {});
      document.removeEventListener('click',   unlock);
      document.removeEventListener('scroll',  unlock);
      document.removeEventListener('keydown', unlock);
    };
    document.addEventListener('click',   unlock, { passive: true });
    document.addEventListener('scroll',  unlock, { passive: true });
    document.addEventListener('keydown', unlock, { passive: true });
  });
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
