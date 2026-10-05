/* Product SKU — live suggestion & validation.
   Mirrors DeChimex.Shared.Constants.ProductSkuGenerator so the suggestions shown to the
   user match exactly what the server accepts (3-char name prefix + '-' + 5-char code).
   The SKU is a suggestion: it is auto-filled from the product name and stays editable. */
(function () {
  'use strict';

  function onReady(fn) {
    if (document.readyState !== 'loading') fn();
    else document.addEventListener('DOMContentLoaded', fn);
  }

  onReady(function () {
    var skuInput = document.querySelector('[data-sku-input]');
    if (!skuInput) return;
    var form = skuInput.closest('form');
    var nameInput = form ? form.querySelector('input[name="Name"]') : null;
    var regenBtn = document.querySelector('[data-sku-regen]');

    var existing = new Set((window.__dechimexExistingSkus || []).map(function (s) {
      return String(s).toUpperCase();
    }));

    var STOP = new Set(['A', 'AN', 'THE', 'OF', 'AND', 'FOR', 'WITH', 'ON', 'IN', 'AT',
      'BY', 'FROM', 'TO', 'OR', 'AS', 'IS', 'ARE', 'IT', 'THIS', 'THAT']);
    var CHARS = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789';

    function prefixFor(name) {
      var upper = (name || '').toUpperCase();
      var tokens = upper.split(/[^A-Z0-9]+/).filter(Boolean);
      var meaningful = tokens.filter(function (t) { return !STOP.has(t); });
      var use = meaningful.length ? meaningful : tokens;
      if (!use.length) return 'PRD';
      var initials = use.map(function (t) { return t.charAt(0); }).join('');
      if (initials.length >= 3) return initials.slice(0, 3);
      var used = {};
      initials.split('').forEach(function (c) { used[c] = 1; });
      var sb = initials;
      var concat = use.join('');
      for (var i = 0; i < concat.length; i++) {
        var ch = concat.charAt(i);
        if (!/[A-Z0-9]/.test(ch) || used[ch]) continue;
        used[ch] = 1;
        sb += ch;
        if (sb.length === 3) break;
      }
      while (sb.length < 3) sb += 'X';
      return sb.slice(0, 3);
    }

    function randSuffix() {
      var s = '';
      var arr = new Uint32Array(5);
      if (window.crypto && crypto.getRandomValues) crypto.getRandomValues(arr);
      for (var i = 0; i < 5; i++) s += CHARS.charAt(arr[i] % CHARS.length);
      return s;
    }

    function buildSku(name) {
      var prefix = prefixFor(name);
      var guard = 0;
      var candidate;
      do { candidate = prefix + '-' + randSuffix(); }
      while (existing.has(candidate) && ++guard < 50);
      return candidate;
    }

    // Normalise what the user types: uppercase, allow only A-Z0-9, keep the hyphen at
    // position 3, and cap at 9 chars. Formats mid-typing without stealing their content.
    function sanitize(raw) {
      var s = (raw || '').toUpperCase().replace(/[^A-Z0-9]/g, '');
      if (s.length > 3) s = s.slice(0, 3) + '-' + s.slice(3);
      return s.slice(0, 9);
    }

    function regenerate() {
      skuInput.value = buildSku(nameInput ? nameInput.value : '');
      skuInput.setAttribute('data-sku-auto', 'true');
    }

    if (nameInput) {
      nameInput.addEventListener('input', function () {
        // Only overwrite while the current value is still an auto-suggestion.
        if (skuInput.getAttribute('data-sku-auto') === 'true') regenerate();
      });
    }

    // A keystroke in the SKU means the user is customising it -> stop regenerating it.
    skuInput.addEventListener('input', function () {
      skuInput.setAttribute('data-sku-auto', 'false');
      var current = skuInput.value;
      var clean = sanitize(current);
      if (clean !== current) skuInput.value = clean;
    });

    if (regenBtn) {
      regenBtn.addEventListener('click', function (e) {
        e.preventDefault();
        regenerate();
      });
    }

    // Initial state: fill when blank; otherwise infer auto vs custom from the prefix.
    if (!skuInput.value) {
      regenerate();
    } else {
      var presentPrefix = (skuInput.value.split('-')[0] || '').toUpperCase();
      var expectedPrefix = prefixFor(nameInput ? nameInput.value : '');
      skuInput.setAttribute('data-sku-auto', presentPrefix === expectedPrefix ? 'true' : 'false');
    }
  });
})();
