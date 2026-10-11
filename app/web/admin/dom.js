// Построение интерфейса БЕЗ вставки HTML-разметки: любой текст из сервера попадает на страницу только как текст
// (createTextNode / textContent), поэтому внедрить в страницу чужую разметку или скрипт нельзя.
// Тест tests/test_admin_web.py следит, чтобы в файлах админки не появлялись опасные способы вставки.
import { phoneDigits, phoneMask } from './format.js';

/** h('div', {class: 'x', onclick: fn}, 'текст', childElement, [ещё, элементы]) */
export function h(tag, props, ...children) {
  const el = document.createElement(tag);
  for (const [key, value] of Object.entries(props || {})) {
    if (value === false || value == null) continue;
    if (key === 'class') el.className = value;
    else if (key.startsWith('on') && typeof value === 'function') el.addEventListener(key.slice(2).toLowerCase(), value);
    else if (key === 'dataset') Object.assign(el.dataset, value);
    else if (key in el && typeof value !== 'object' && isWritable(el, key)) el[key] = value;
    else el.setAttribute(key, value === true ? '' : String(value));
  }
  append(el, children);
  return el;
}

/** Свойство элемента можно присвоить? У input.list, например, есть только чтение: такое задаём атрибутом, иначе страница падает с ошибкой. */
export function isWritable(el, key) {
  for (let proto = el; proto; proto = Object.getPrototypeOf(proto)) {
    const d = Object.getOwnPropertyDescriptor(proto, key);
    if (d) return !!d.set || d.writable === true;
  }
  return true;
}

export function append(el, children) {
  for (const child of children.flat(Infinity)) {
    if (child == null || child === false) continue;
    el.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return el;
}

/** Заменяет содержимое контейнера. */
export function setChildren(el, ...children) {
  el.replaceChildren();
  return append(el, children);
}

/** Поле телефона: только цифры, номер сам собирается в «+7 (900) 123-45-67», пока пусто видна подсказка. Стирание работает с конца, скобки не мешают. */
export function phoneInput(props = {}) {
  const input = h('input', { type: 'tel', inputMode: 'numeric', autocomplete: 'off', maxLength: 18, placeholder: '+7 (000) 000-00-00', ...props });
  let before = phoneDigits(input.value);
  input.addEventListener('beforeinput', (ev) => {
    if (ev.inputType === 'insertText' && ev.data && /\D/.test(ev.data)) ev.preventDefault();   // буквы, пробелы и знаки не вводятся
  });
  input.addEventListener('input', (ev) => {
    let digits = phoneDigits(input.value);
    if (String(ev.inputType || '').startsWith('delete') && digits.length === before.length) digits = digits.slice(0, -1);   // стёрли скобку или пробел: убираем последнюю цифру
    if (/^[78]$/.test(input.value.trim()) && !before) digits = '';
    const text = phoneMask(digits);
    if (input.value !== text) input.value = text;
    before = digits;
  });
  return input;
}

/** Поиск или код: только цифры. */
export function digitsInput(props = {}) {
  const input = h('input', { type: 'search', inputMode: 'numeric', autocomplete: 'off', ...props });
  input.addEventListener('beforeinput', (ev) => {
    if (ev.inputType === 'insertText' && ev.data && /\D/.test(ev.data)) ev.preventDefault();
  });
  input.addEventListener('input', () => { const text = input.value.replace(/\D/g, ''); if (input.value !== text) input.value = text; });
  return input;
}

export const badge = (text, kind = '') => h('span', { class: `badge ${kind}`.trim() }, text);

export function field(label, input, hint) {
  return h('label', { class: 'field' }, h('span', {}, label), input, hint ? h('div', { class: 'small muted' }, hint) : null);
}

export const emptyState = (text) => h('div', { class: 'empty' }, text);

/** Экран входа для админки клуба и панели платформы: отдельная рамка по центру окна, за ней лёгкие золотые кольца. */
export function loginScreen(root, { brand, title, intro, form }) {
  setChildren(root,
    h('div', { class: 'login-wrap' },
      h('div', { class: 'login-rings', 'aria-hidden': 'true' }, h('i', { class: 'ring r1' }), h('i', { class: 'ring r2' }), h('i', { class: 'ring r3' })),
      h('section', { class: 'login-card' },
        h('div', { class: 'bar' }),
        h('span', { class: 'brand' }, brand),
        h('h1', {}, title),
        h('p', { class: 'muted' }, intro),
        form)));
}

// ---------- окна ----------
let activeModals = 0;

/**
 * Модальное окно. content — элемент или массив; actions — кнопки внизу.
 * Закрывается по Esc, по клику на фон и по крестику. Возвращает { close, el }.
 */
export function openModal({ title, content, actions = [], wide = false, onClose }) {
  const previous = document.activeElement;
  const backdrop = h('div', { class: 'modal-backdrop' });
  const close = () => {
    if (!backdrop.isConnected) return;
    backdrop.remove();
    document.removeEventListener('keydown', onKey, true);
    activeModals--;
    if (previous && previous.focus) previous.focus();
    if (onClose) onClose();
  };
  const onKey = (event) => {
    if (event.key === 'Escape' && backdrop === lastBackdrop()) { event.stopPropagation(); close(); }
  };
  const dialog = h('div', { class: wide ? 'modal wide' : 'modal', role: 'dialog', 'aria-modal': 'true', 'aria-label': title, tabIndex: -1 },
    h('div', { class: 'modal-head' }, h('h2', {}, title),
      h('button', { class: 'icon-btn', type: 'button', 'aria-label': 'Закрыть', onclick: close }, '×')),
    h('div', { class: 'modal-body' }, content),
    actions.length ? h('div', { class: 'modal-actions' }, actions) : null);
  backdrop.append(dialog);
  backdrop.addEventListener('mousedown', (event) => { if (event.target === backdrop) close(); });
  document.body.append(backdrop);
  document.addEventListener('keydown', onKey, true);
  activeModals++;
  (dialog.querySelector('input:not([type=hidden]), select, textarea') || dialog).focus();
  return { close, el: dialog };
}

function lastBackdrop() {
  const all = document.querySelectorAll('.modal-backdrop');
  return all[all.length - 1];
}

/** Вопрос «Вы уверены?». Возвращает Promise<boolean>. */
export function confirmDialog({ title, text, okText = 'Подтвердить', danger = false }) {
  return new Promise((resolve) => {
    let answered = false;
    const answer = (value) => { answered = true; modal.close(); resolve(value); };
    const ok = h('button', { class: danger ? 'btn btn-danger' : 'btn btn-primary', type: 'button', onclick: () => answer(true) }, okText);
    const modal = openModal({
      title, content: h('p', {}, text),
      actions: [h('button', { class: 'btn', type: 'button', onclick: () => answer(false) }, 'Отмена'), ok],
      onClose: () => { if (!answered) resolve(false); },
    });
    ok.focus();
  });
}

// ---------- уведомления ----------
export function toast(message, kind = '') {
  let box = document.querySelector('.toasts');
  if (!box) { box = h('div', { class: 'toasts', 'aria-live': 'polite' }); document.body.append(box); }
  const item = h('div', { class: `toast ${kind}`.trim(), role: kind === 'error' ? 'alert' : 'status' }, message);
  box.append(item);
  setTimeout(() => item.remove(), kind === 'error' ? 7000 : 3500);
}

/** Копирует текст в буфер обмена; возвращает true, если получилось. */
export async function copyText(text) {
  try { await navigator.clipboard.writeText(text); return true; } catch { return false; }
}

export const anyModalOpen = () => activeModals > 0;
