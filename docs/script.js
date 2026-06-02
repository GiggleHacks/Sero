/* ── Nav scroll ── */
window.addEventListener('scroll', () => {
  document.getElementById('nav').classList.toggle('scrolled', window.scrollY > 40);
}, { passive: true });

/* ── Scroll reveal ── */
const revealIO = new IntersectionObserver(entries => {
  entries.forEach(({ isIntersecting, target }) => {
    if (isIntersecting) {
      target.classList.add('visible');
      revealIO.unobserve(target);
    }
  });
}, { threshold: 0.07 });

document.querySelectorAll('.reveal').forEach((el, i) => {
  el.style.transitionDelay = (i % 4) * 60 + 'ms';
  revealIO.observe(el);
});

/* ── Music player ── */
(function () {
  const audio    = document.getElementById('bg-music');
  const btn      = document.getElementById('player-btn');
  const iconPlay  = document.getElementById('icon-play');
  const iconPause = document.getElementById('icon-pause');
  const progress = document.getElementById('player-progress');
  const player   = document.getElementById('player');
  if (!audio || !btn) return;

  audio.volume = 0.32;
  let started = false;

  function setPlaying(p) {
    if (iconPlay)  iconPlay.style.display  = p ? 'none'  : 'block';
    if (iconPause) iconPause.style.display = p ? 'block' : 'none';
    if (player)    player.classList.toggle('active', p);
  }

  function start() {
    if (started) return;
    started = true;
    audio.play().then(() => setPlaying(true)).catch(() => {});
  }

  document.addEventListener('click',  start, { once: true, passive: true });
  document.addEventListener('scroll', start, { once: true, passive: true });

  btn.addEventListener('click', e => {
    e.stopPropagation();
    if (!started) { start(); return; }
    if (audio.paused) {
      audio.play().then(() => setPlaying(true)).catch(() => {});
    } else {
      audio.pause();
      setPlaying(false);
    }
  });

  audio.addEventListener('timeupdate', () => {
    if (audio.duration && progress)
      progress.style.width = (audio.currentTime / audio.duration * 100) + '%';
  }, { passive: true });
})();

/* ── Smooth anchor scroll ── */
document.querySelectorAll('a[href^="#"]').forEach(a => {
  a.addEventListener('click', e => {
    const t = document.querySelector(a.getAttribute('href'));
    if (t) { e.preventDefault(); t.scrollIntoView({ behavior: 'smooth' }); }
  });
});
