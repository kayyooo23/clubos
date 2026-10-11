import { h, setChildren, badge, openModal, confirmDialog, toast, field, emptyState, digitsInput } from '../dom.js';
import { get, post } from '../api.js';
import { topupDialog } from './topup.js';
import { money, phonePretty, fmtDateTime, txKindName } from '../format.js';

const LIMIT = 100;

export function mount(root, ctx) {
  const offset = ctx.club.utc_offset;
  const search = digitsInput({ placeholder: 'Поиск по номеру (от 3 цифр)', 'aria-label': 'Поиск гостя по номеру телефона' });
  const hint = h('div', { class: 'small muted' });
  const box = h('div', { class: 'table-wrap' });
  root.append(
    h('div', { class: 'page-head' }, h('h1', {}, 'Гости'), h('div', { class: 'search-box' }, search), h('span', { class: 'spacer' }), hint),
    box);

  let timer;
  let version = 0;   // защита от «медленный старый ответ перекрыл новый»
  search.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(load, 250); });

  async function load() {
    const digits = search.value.replace(/\D/g, '');
    const mine = ++version;
    if (search.value && digits.length < 3) { hint.textContent = 'Введите хотя бы 3 цифры'; return; }
    try {
      const list = await get(`/api/admin/accounts?limit=${LIMIT}${digits ? `&q=${encodeURIComponent(digits)}` : ''}`);
      if (mine !== version) return;
      hint.textContent = list.length >= LIMIT ? `Показаны последние ${LIMIT}. Уточните поиск.` : `Найдено: ${list.length}`;
      render(list);
    } catch (e) { hint.textContent = ''; toast(e.message, 'error'); }
  }

  function render(list) {
    if (list.length === 0) { setChildren(box, emptyState(search.value ? 'Никого не нашли' : 'Гостей пока нет: они регистрируются на экране ПК.')); return; }
    setChildren(box, h('table', {},
      h('thead', {}, h('tr', {}, ['Телефон', 'Баланс', 'Статус', 'Регистрация'].map((t) => h('th', { class: t === 'Баланс' ? 'right' : '' }, t)))),
      h('tbody', {}, list.map((acc) => h('tr', { class: 'clickable', tabIndex: 0, onclick: () => guestModal(acc, load), onkeydown: (e) => { if (e.key === 'Enter') guestModal(acc, load); } },
        h('td', {}, h('b', {}, phonePretty(acc.phone))),
        h('td', { class: 'right' }, money(acc.balance)),
        h('td', {}, acc.status === 'active' ? badge('активен', 'ok') : badge('заблокирован', 'danger')),
        h('td', { class: 'muted' }, fmtDateTime(acc.created_at, offset)))))));
  }

  // ---------- карточка гостя ----------
  function guestModal(first, onChanged) {
    let acc = first;
    const balanceEl = h('div', { class: 'big-balance' });
    const statusEl = h('span', {});
    const actionsEl = h('div', { class: 'row' });
    const history = h('div', { class: 'table-wrap' });

    function paint() {
      balanceEl.textContent = money(acc.balance);
      setChildren(statusEl, acc.status === 'active' ? badge('активен', 'ok') : badge('заблокирован', 'danger'));
      setChildren(actionsEl,
        h('button', { class: 'btn btn-primary', type: 'button', disabled: acc.status !== 'active', onclick: () => topupModal(acc, reload) }, 'Пополнить'),
        h('button', { class: 'btn', type: 'button', onclick: () => adjustModal(acc, reload) }, 'Корректировка'),
        acc.status === 'active'
          ? h('button', { class: 'btn btn-danger', type: 'button', onclick: () => toggleBlock(true) }, 'Заблокировать')
          : h('button', { class: 'btn', type: 'button', onclick: () => toggleBlock(false) }, 'Разблокировать'));
    }

    async function reload() {
      const found = await get(`/api/admin/accounts?phone=${encodeURIComponent(acc.phone)}`);
      if (found[0]) acc = found[0];
      paint();
      const tx = await get(`/api/admin/accounts/${acc.id}/transactions?limit=50`);
      if (tx.length === 0) { setChildren(history, emptyState('Операций пока нет')); }
      else {
        setChildren(history, h('table', {},
          h('thead', {}, h('tr', {}, ['Время', 'Операция', 'Сумма', 'Баланс', 'Комментарий'].map((t) => h('th', { class: t === 'Сумма' || t === 'Баланс' ? 'right' : '' }, t)))),
          h('tbody', {}, tx.map((t) => h('tr', {},
            h('td', { class: 'muted' }, fmtDateTime(t.created_at, offset)),
            h('td', {}, txKindName(t.kind)),
            h('td', { class: `right ${t.amount > 0 ? 'ok' : 'danger'}` }, `${t.amount > 0 ? '+' : '−'}${money(Math.abs(t.amount))}`),
            h('td', { class: 'right' }, money(t.balance_after)),
            h('td', { class: 'wrap muted' }, t.comment || ''))))));
      }
      if (onChanged) onChanged();
    }

    async function toggleBlock(block) {
      const ok = await confirmDialog({
        title: block ? 'Заблокировать гостя?' : 'Разблокировать гостя?',
        text: block ? `${phonePretty(acc.phone)} не сможет входить; идущая сессия будет завершена.` : `${phonePretty(acc.phone)} снова сможет входить и играть.`,
        okText: block ? 'Заблокировать' : 'Разблокировать', danger: block,
      });
      if (!ok) return;
      await post(`/api/admin/accounts/${acc.id}/${block ? 'block' : 'unblock'}`);
      toast(block ? 'Гость заблокирован' : 'Гость разблокирован', 'ok');
      reload();
    }

    openModal({
      title: phonePretty(acc.phone), wide: true,
      content: [h('div', { class: 'row' }, balanceEl, h('span', { class: 'spacer' }), statusEl), actionsEl,
        h('h3', { class: 'section-title' }, 'Последние операции'), history],
    });
    paint();
    reload().catch((e) => toast(e.message, 'error'));
  }

  // ---------- деньги ----------
  function topupModal(acc, done) {
    topupDialog({ title: `Пополнить: ${phonePretty(acc.phone)}`, balance: acc.balance, url: `/api/admin/accounts/${acc.id}/topup`, done });
  }

  function adjustModal(acc, done) {
    const amount = h('input', { type: 'number', min: -100000, max: 100000, step: 1, inputMode: 'numeric', placeholder: 'Например, −200 или 150', required: true });
    const comment = h('input', { maxLength: 200, placeholder: 'Обязательно: что исправляем', required: true });
    const preview = h('div', { class: 'small muted' });
    const error = h('div', { class: 'error-text', role: 'alert' });
    const submit = h('button', { class: 'btn btn-primary', type: 'button', disabled: true, onclick: save }, 'Применить');
    const value = () => { const n = Number(amount.value); return Number.isInteger(n) && n !== 0 && Math.abs(n) <= 100000 ? n : 0; };
    const sync = () => {
      const n = value();
      const after = acc.balance + n;
      preview.textContent = n ? `Баланс будет: ${money(after)}` : '';
      preview.className = after < 0 ? 'small danger' : 'small muted';
      submit.disabled = !n || !comment.value.trim() || after < 0;
    };
    amount.addEventListener('input', sync);
    comment.addEventListener('input', sync);

    async function save() {
      error.textContent = '';
      submit.disabled = true;
      try {
        const res = await post(`/api/admin/accounts/${acc.id}/adjust`, { amount: value(), comment: comment.value.trim() });
        modal.close();
        toast(`Баланс исправлен: ${money(res.balance)}`, 'ok');
        done();
      } catch (e) { error.textContent = e.message; sync(); }
    }

    const modal = openModal({
      title: `Корректировка: ${phonePretty(acc.phone)}`,
      content: h('form', { onsubmit: (ev) => { ev.preventDefault(); if (!submit.disabled) save(); } },
        h('p', { class: 'small muted' }, 'Для исправления ошибок. Положительное число добавит деньги, отрицательное спишет. Запись попадёт в журнал с вашим именем.'),
        h('div', { class: 'muted' }, 'Сейчас на балансе: ', h('b', {}, money(acc.balance))),
        field('Сумма, ₽ (со знаком)', amount), preview, field('Причина', comment), error),
      actions: [h('button', { class: 'btn', type: 'button', onclick: () => modal.close() }, 'Отмена'), submit],
    });
  }

  load();
  return { unmount() { clearTimeout(timer); version++; } };
}
