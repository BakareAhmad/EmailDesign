/* Customer shell behaviours: mobile search toggle, account menu, bottom sheets,
   quantity steppers, password reveal. Plain JS, no dependencies (~2 KB). */
(function () {
  'use strict';
  var d = document;
  function $(id) { return d.getElementById(id); }
  function on(el, ev, fn) { if (el) el.addEventListener(ev, fn); }

  /* ---- mobile search ---- */
  var sToggle = $('csSearchToggle'), sForm = $('csMobileSearch');
  on(sToggle, 'click', function () {
    var open = sForm.hasAttribute('hidden');
    if (open) { sForm.removeAttribute('hidden'); var i = sForm.querySelector('input'); if (i) i.focus(); }
    else sForm.setAttribute('hidden', '');
    sToggle.setAttribute('aria-expanded', open ? 'true' : 'false');
  });

  /* ---- account menu (sheet on phones, dropdown on desktop) ---- */
  var mBtn = $('csMenuBtn'), menu = $('csMenu'), scrim = $('csScrim');
  function closeMenu() {
    if (!menu) return;
    menu.setAttribute('hidden', '');
    if (mBtn) mBtn.setAttribute('aria-expanded', 'false');
    if (scrim) scrim.classList.remove('is-open');
  }
  on(mBtn, 'click', function (e) {
    e.stopPropagation();
    var open = menu.hasAttribute('hidden');
    if (open) { menu.removeAttribute('hidden'); mBtn.setAttribute('aria-expanded', 'true'); if (scrim) scrim.classList.add('is-open'); }
    else closeMenu();
  });
  on(scrim, 'click', closeMenu);
  on(d, 'click', function (e) { if (menu && !menu.hasAttribute('hidden') && !menu.contains(e.target)) closeMenu(); });

  /* ---- generic sheets: [data-sheet-open="id"] / [data-sheet-close] ---- */
  function openSheet(id) { var s = $(id); if (!s) return; s.classList.add('is-open'); d.body.style.overflow = 'hidden'; var f = s.querySelector('button,[href],input,select'); if (f) f.focus(); }
  function closeSheets() { Array.prototype.forEach.call(d.querySelectorAll('.sh-sheet.is-open'), function (s) { s.classList.remove('is-open'); }); d.body.style.overflow = ''; }
  on(d, 'click', function (e) {
    var o = e.target.closest('[data-sheet-open]');
    if (o) { e.preventDefault(); openSheet(o.getAttribute('data-sheet-open')); return; }
    if (e.target.closest('[data-sheet-close]') || e.target.classList.contains('sh-sheet-bg')) { closeSheets(); }
  });
  on(d, 'keydown', function (e) { if (e.key === 'Escape') { closeMenu(); closeSheets(); var lb = d.querySelector('.pd-lightbox.is-open'); if (lb) lb.classList.remove('is-open'); } });

  /* ---- quantity steppers ---- */
  on(d, 'click', function (e) {
    var b = e.target.closest('[data-qty]');
    if (!b) return;
    var wrap = b.parentElement, input = wrap && wrap.querySelector('input[type=number]');
    if (!input) return;
    var v = parseInt(input.value, 10) || 1, min = parseInt(input.min, 10) || 1, max = parseInt(input.max, 10) || 10000;
    v += b.getAttribute('data-qty') === '+' ? 1 : -1;
    input.value = Math.min(max, Math.max(min, v));
    input.dispatchEvent(new Event('change', { bubbles: true }));
    if (b.hasAttribute('data-qty-submit') && !input.hasAttribute('data-submit-on-change')) { var f = input.form; if (f && f.requestSubmit) f.requestSubmit(); else if (f) f.submit(); }
  });

  /* ---- password reveal ---- */
  on(d, 'click', function (e) {
    var b = e.target.closest('[data-reveal],[data-pw-toggle]');
    if (!b) return;
    var i = b.hasAttribute('data-reveal') ? $(b.getAttribute('data-reveal')) : b.parentElement.querySelector('input');
    if (!i) return;
    var show = i.type === 'password';
    i.type = show ? 'text' : 'password';
    b.textContent = show ? 'Hide' : 'Show';
    b.setAttribute('aria-pressed', show ? 'true' : 'false');
  });

  /* ---- auto-submit controls (sort selects, cart quantity inputs) ---- */
  on(d, 'change', function (e) {
    var s = e.target.closest('select[data-autosubmit],input[data-submit-on-change]');
    if (s && s.form) { if (s.form.requestSubmit) s.form.requestSubmit(); else s.form.submit(); }
  });
})();
