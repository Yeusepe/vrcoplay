// Copyright (c) 2026 YUCP Studio. SPDX-License-Identifier: GPL-3.0-or-later.
import { TextMorph } from './vendor/torph/torph.js';

for (const button of document.querySelectorAll('[data-copy]')) {
  let reset;
  button.hidden = false;
  const label = button.textContent;
  const morph = new TextMorph({ element: button.firstElementChild, duration: 280, scale: false });
  morph.update(label);
  button.addEventListener('click', async () => {
    const status = document.querySelector('#copy-status');
    const fallback = document.querySelector('#copy-fallback');
    clearTimeout(reset);
    status.textContent = '';
    try {
      await navigator.clipboard.writeText(button.dataset.copy);
      fallback.hidden = true;
      button.classList.add('copied');
      morph.update('✓ Copied');
      status.textContent = `Copied ${button.dataset.label}.`;
      reset = setTimeout(() => { button.classList.remove('copied'); morph.update(label); }, 2500);
    } catch {
      button.classList.remove('copied');
      morph.update(label);
      button.after(fallback);
      fallback.hidden = false;
      fallback.querySelector('label').textContent = `Copy ${button.dataset.label}:`;
      const input = fallback.querySelector('input');
      input.value = button.dataset.copy;
      input.focus();
      input.select();
      status.textContent = 'Select and copy the value below.';
    }
  });
}
