/* Product page: thumbnail switching, swipe between images, lightbox, copy link. */
(function () {
  'use strict';
  var d = document, stage = d.getElementById('pdStageImg');
  var thumbs = Array.prototype.slice.call(d.querySelectorAll('.pd-thumb'));
  var dots = Array.prototype.slice.call(d.querySelectorAll('#pdDots i'));
  var idx = 0;
  function show(i) {
    if (!stage || !thumbs.length) return;
    idx = (i + thumbs.length) % thumbs.length;
    stage.src = thumbs[idx].getAttribute('data-src');
    stage.alt = thumbs[idx].getAttribute('data-alt') || '';
    thumbs.forEach(function (t, k) { t.classList.toggle('is-on', k === idx); t.setAttribute('aria-selected', k === idx ? 'true' : 'false'); });
    dots.forEach(function (o, k) { o.classList.toggle('is-on', k === idx); });
  }
  thumbs.forEach(function (t, k) { t.addEventListener('click', function () { show(k); }); });
  var g = d.getElementById('pdGallery'), x0 = null;
  if (g && thumbs.length > 1) {
    g.addEventListener('touchstart', function (e) { x0 = e.touches[0].clientX; }, { passive: true });
    g.addEventListener('touchend', function (e) {
      if (x0 === null) return;
      var dx = e.changedTouches[0].clientX - x0; x0 = null;
      if (Math.abs(dx) > 40) show(idx + (dx < 0 ? 1 : -1));
    }, { passive: true });
  }
  var lb = d.getElementById('pdLightbox'), lbImg = d.getElementById('pdLightboxImg'), zoom = d.getElementById('pdZoom');
  function openLb() { if (!stage || !lb) return; lbImg.src = stage.src; lb.classList.add('is-open'); d.body.style.overflow = 'hidden'; }
  function closeLb() { if (!lb) return; lb.classList.remove('is-open'); d.body.style.overflow = ''; }
  if (zoom) zoom.addEventListener('click', openLb);
  if (stage) stage.addEventListener('click', openLb);
  var lbClose = d.getElementById('pdLightboxClose');
  if (lbClose) lbClose.addEventListener('click', closeLb);
  if (lb) lb.addEventListener('click', function (e) { if (e.target === lb) closeLb(); });
  var copy = d.getElementById('pdCopy');
  if (copy) copy.addEventListener('click', function () {
    var url = copy.getAttribute('data-copy'), done = function () { var t = copy.innerHTML; copy.textContent = 'Link copied'; setTimeout(function () { copy.innerHTML = t; }, 1800); };
    // Web Share first where available (PWA): shares the public product link only.
    if (navigator.share) {
      navigator.share({ title: document.title, url: url }).then(done).catch(function () { /* user cancelled */ });
      return;
    }
    if (navigator.clipboard && navigator.clipboard.writeText) navigator.clipboard.writeText(url).then(done, function () { window.prompt('Copy this link', url); });
    else window.prompt('Copy this link', url);
  });
  d.body.classList.add('has-pd-sticky');
})();
