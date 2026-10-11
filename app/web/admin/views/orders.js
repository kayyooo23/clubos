import { h, setChildren, badge, confirmDialog, toast, emptyState } from '../dom.js';
import { get, post } from '../api.js';
import { topupByPc } from './topup.js';
import { money, phonePretty, fmtDateTime, parseUtc } from '../format.js';

const REFRESH_MS = 4000;
const STATUS = { new: ['новый', 'live'], accepted: ['готовится', 'accent'], done: ['выдан', 'ok'], cancelled: ['отменён', ''] };

const waiting = (value) => {
  const d = parseUtc(value);
  if (!d) return '';
  const min = Math.max(0, Math.round((Date.now() - d.getTime()) / 60000));
  return min < 1 ? 'только что' : `${min} мин назад`;
};

export function mount(root, ctx) {
  const offset = ctx.club.utc_offset;
  let showAll = false;
  const callsBox = h('div', {});
  const ordersBox = h('div', {});
  const toggle = h('button', { class: 'btn btn-sm', type: 'button', onclick: () => { showAll = !showAll; toggle.textContent = label(); load(); } }, '');
  const label = () => (showAll ? 'Только в работе' : 'Показать историю');
  toggle.textContent = label();

  root.append(
    h('div', { class: 'page-head' }, h('h1', {}, 'Заказы'), h('span', { class: 'spacer' }), toggle),
    h('h2', { class: 'section-title' }, 'Вызовы администратора'), callsBox,
    h('h2', { class: 'section-title' }, 'Заказы из бара'), ordersBox);

  async function load() {
    try {
      const [calls, orders] = await Promise.all([
        get('/api/admin/calls?open_only=true'),
        get(`/api/admin/orders?status=${showAll ? 'all' : 'open'}&limit=100`),
      ]);
      renderCalls(calls);
      renderOrders(orders);
    } catch (e) { toast(e.message, 'error'); }
  }

  function renderCalls(calls) {
    if (calls.length === 0) { setChildren(callsBox, emptyState('Никто не зовёт')); return; }
    setChildren(callsBox, h('div', { class: 'table-wrap' }, h('table', {},
      h('tbody', {}, calls.map((c) => h('tr', {},
        h('td', {}, h('b', {}, c.pc_name)),
        h('td', { class: 'muted' }, phonePretty(c.guest_phone || '')),
        h('td', { class: 'wrap' }, c.text || 'Зовёт администратора'),
        h('td', { class: 'muted' }, waiting(c.created_at)),
        h('td', { class: 'right' },
          h('button', { class: 'btn btn-sm', type: 'button', title: 'Принять наличные у гостя за этим ПК', onclick: () => topupByPc({ id: c.pc_id, name: c.pc_name }, load) }, 'Пополнить'), ' ',
          h('button', { class: 'btn btn-primary btn-sm', type: 'button', onclick: () => finishCall(c) }, 'Подошёл'))))))));
  }

  function renderOrders(orders) {
    if (orders.length === 0) { setChildren(ordersBox, emptyState(showAll ? 'Заказов пока не было' : 'Нет заказов в работе')); return; }
    setChildren(ordersBox, h('div', { class: 'table-wrap' }, h('table', {},
      h('thead', {}, h('tr', {}, ['ПК', 'Что', 'Сумма', 'Когда', 'Статус', ''].map((t, i) => h('th', { class: i === 2 ? 'right' : '' }, t)))),
      h('tbody', {}, orders.map((o) => h('tr', { class: o.status === 'cancelled' ? 'dim' : '' },
        h('td', {}, h('b', {}, o.pc_name), h('div', { class: 'small muted' }, phonePretty(o.guest_phone || ''))),
        h('td', { class: 'wrap' }, o.items.map((i) => `${i.name} x ${i.qty}`).join(', '),
          o.comment ? h('div', { class: 'small muted' }, `Комментарий: ${o.comment}`) : null),
        h('td', { class: 'right' }, money(o.total)),
        h('td', { class: 'muted' }, fmtDateTime(o.created_at, offset), h('div', { class: 'small' }, waiting(o.created_at))),
        h('td', {}, badge(STATUS[o.status][0], STATUS[o.status][1])),
        h('td', { class: 'right' }, actions(o))))))));
  }

  function actions(o) {
    const buttons = [];
    if (o.status === 'new') buttons.push(h('button', { class: 'btn btn-sm', type: 'button', onclick: () => move(o, 'accepted') }, 'Принять'));
    if (o.status === 'new' || o.status === 'accepted') {
      buttons.push(h('button', { class: 'btn btn-primary btn-sm', type: 'button', onclick: () => move(o, 'done') }, 'Выдан'));
      buttons.push(h('button', { class: 'btn btn-danger btn-sm', type: 'button', onclick: () => cancel(o) }, 'Отменить'));
    }
    return buttons.flatMap((b, i) => (i ? [' ', b] : [b]));
  }

  async function move(o, status) {
    try {
      await post(`/api/admin/orders/${o.id}/status`, { status });
      load();
    } catch (e) { toast(e.message, 'error'); load(); }
  }

  async function cancel(o) {
    const ok = await confirmDialog({
      title: `Отменить заказ для ${o.pc_name}?`,
      text: `Гостю вернутся ${money(o.total)} на баланс.`, okText: 'Отменить заказ', danger: true,
    });
    if (ok) move(o, 'cancelled');
  }

  async function finishCall(c) {
    try {
      await post(`/api/admin/calls/${c.id}/done`);
      load();
    } catch (e) { toast(e.message, 'error'); }
  }

  const timer = setInterval(() => { if (!document.hidden) load(); }, REFRESH_MS);
  load();
  return { unmount() { clearInterval(timer); } };
}
