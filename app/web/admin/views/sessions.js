import { h, setChildren, badge, toast, emptyState } from '../dom.js';
import { get } from '../api.js';
import { money, phonePretty, fmtDateTime, durationText } from '../format.js';

const REFRESH_MS = 10000;

export function mount(root, ctx) {
  const offset = ctx.club.utc_offset;
  const box = h('div', { class: 'table-wrap' });
  root.append(
    h('div', { class: 'page-head' }, h('h1', {}, 'Сессии'), h('span', { class: 'spacer' }),
      h('button', { class: 'btn btn-sm', type: 'button', onclick: load }, 'Обновить')),
    box);

  async function load() {
    try {
      const list = await get('/api/admin/sessions?limit=100');
      if (list.length === 0) { setChildren(box, emptyState('Сессий пока не было')); return; }
      setChildren(box, h('table', {},
        h('thead', {}, h('tr', {}, ['Начало', 'ПК', 'Гость', 'Тариф', 'Длительность', 'Стоимость', 'Статус'].map((t) => h('th', { class: t === 'Стоимость' ? 'right' : '' }, t)))),
        h('tbody', {}, list.map((s) => h('tr', {},
          h('td', { class: 'muted' }, fmtDateTime(s.started_at, offset)),
          h('td', {}, s.pc_name || `№${s.pc_id}`),
          h('td', {}, phonePretty(s.guest_phone || '') || '-'),
          h('td', {}, s.tariff),
          h('td', { class: 'muted' }, durationText(s.started_at, s.ended_at || new Date().toISOString())),
          h('td', { class: 'right' }, money(s.cost)),
          h('td', {}, s.ended_at ? badge('завершена') : badge('идёт', 'ok')))))));
    } catch (e) { toast(e.message, 'error'); }
  }

  const timer = setInterval(() => { if (!document.hidden) load(); }, REFRESH_MS);
  load();
  return { unmount() { clearInterval(timer); } };
}
