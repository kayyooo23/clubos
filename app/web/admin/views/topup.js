// Приём наличных: одно окно и для «Гостей» (по номеру телефона), и для карты клуба (по номеру ПК), и для списка вызовов.
import { h, openModal, toast, field } from '../dom.js';
import { get, post } from '../api.js';
import { money, phonePretty } from '../format.js';

/**
 * Окно «принять наличные». url: куда отправить пополнение; balance: что на балансе сейчас; who: подпись над суммой (необязательно);
 * done: что сделать после успешного пополнения (обновить список).
 */
export function topupDialog({ title, balance, who, url, max = 100000, done }) {
  const amount = h('input', { type: 'number', min: 1, max, step: 1, inputMode: 'numeric', placeholder: 'Сколько приняли, ₽', required: true });
  const comment = h('input', { maxLength: 200, placeholder: 'Необязательно' });
  const after = h('div', { class: 'small muted' });
  const error = h('div', { class: 'error-text', role: 'alert' });
  const submit = h('button', { class: 'btn btn-primary', type: 'button', disabled: true, onclick: save }, 'Принять');
  const value = () => { const n = Number(amount.value); return Number.isInteger(n) && n > 0 && n <= max ? n : 0; };
  const sync = () => {
    submit.disabled = !value();
    submit.textContent = value() ? `Принять ${money(value())} наличными` : 'Принять';
    after.textContent = value() ? `После пополнения на балансе будет ${money(balance + value())}` : '';
  };
  amount.addEventListener('input', sync);

  async function save() {
    error.textContent = '';
    submit.disabled = true;
    try {
      const res = await post(url, { amount: value(), comment: comment.value.trim() || null });
      const now = res.guest ? res.guest.balance : res.balance;
      modal.close();
      toast(`Пополнено на ${money(value())}. Баланс: ${money(now)}`, 'ok');
      if (done) done();
    } catch (e) { error.textContent = e.message; sync(); }
  }

  const modal = openModal({
    title,
    content: h('form', { onsubmit: (ev) => { ev.preventDefault(); if (value()) save(); } },
      who ? h('div', {}, who) : null,
      h('div', { class: 'muted' }, 'Сейчас на балансе: ', h('b', {}, money(balance))),
      field('Сумма, ₽', amount),
      h('div', { class: 'quick' }, [100, 200, 300, 500, 1000].map((n) => h('button', {
        class: 'btn btn-sm', type: 'button', onclick: () => { amount.value = n; sync(); amount.focus(); } }, `+${n}`))),
      after,
      field('Комментарий', comment), error),
    actions: [h('button', { class: 'btn', type: 'button', onclick: () => modal.close() }, 'Отмена'), submit],
  });
  amount.focus();
}

/** Пополнение по номеру ПК: узнаём, кто сейчас за ним сидит (идёт сессия или он вошёл в кабинет), и открываем то же окно. */
export async function topupByPc(pc, done) {
  let info;
  try { info = await get(`/api/admin/pcs/${pc.id}/guest`); } catch (e) { toast(e.message, 'error'); return; }
  if (!info.guest) {
    const modal = openModal({
      title: `Пополнить баланс: ${info.pc.name}`,
      content: h('p', {}, `За ${info.pc.name} сейчас нет гостя: никто не вошёл и сессия не идёт. Если гость стоит у стойки, найдите его в разделе «Гости» по номеру телефона.`),
      actions: [h('button', { class: 'btn', type: 'button', onclick: () => modal.close() }, 'Закрыть'),
        h('a', { class: 'btn btn-primary', href: '#/guests', onclick: () => modal.close() }, 'Открыть гостей')],
    });
    return;
  }
  topupDialog({
    title: `Пополнить баланс: ${info.pc.name}`,
    who: ['Гость ', h('b', {}, phonePretty(info.guest.phone))],
    balance: info.guest.balance, url: `/api/admin/pcs/${pc.id}/topup`, max: info.max_topup, done,
  });
}
