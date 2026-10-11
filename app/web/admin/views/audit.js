import { h, setChildren, toast, emptyState } from '../dom.js';
import { get } from '../api.js';
import { phonePretty, fmtDateTime, actionLabel, auditDetails, ACTION_KEYS } from '../format.js';

export function mount(root, ctx) {
  const offset = ctx.club.utc_offset;
  const filter = h('select', { 'aria-label': 'Тип события' },
    h('option', { value: '' }, 'Все события'),
    ACTION_KEYS.map((k) => h('option', { value: k }, actionLabel(k))));
  const box = h('div', { class: 'table-wrap' });
  let rows = [];
  root.append(
    h('div', { class: 'page-head' }, h('h1', {}, 'Журнал действий'), h('div', { class: 'search-box' }, filter), h('span', { class: 'spacer' }),
      h('button', { class: 'btn btn-sm', type: 'button', onclick: load }, 'Обновить')),
    h('p', { class: 'muted small' }, 'Кто и когда входил на ПК, принимал деньги, менял баланс и ключи. Записи нельзя изменить или удалить.'),
    box);
  filter.addEventListener('change', render);

  async function load() {
    try { rows = await get('/api/admin/audit?limit=300'); render(); } catch (e) { toast(e.message, 'error'); }
  }

  function render() {
    const shown = filter.value ? rows.filter((r) => r.action === filter.value) : rows;
    if (shown.length === 0) { setChildren(box, emptyState('Записей нет')); return; }
    setChildren(box, h('table', {},
      h('thead', {}, h('tr', {}, ['Время', 'Событие', 'Кто', 'ПК', 'Детали'].map((t) => h('th', {}, t)))),
      h('tbody', {}, shown.map((r) => h('tr', {},
        h('td', { class: 'muted' }, fmtDateTime(r.created_at, offset)),
        h('td', {}, actionLabel(r.action)),
        h('td', {}, r.actor_phone ? phonePretty(r.actor_phone) : h('span', { class: 'muted' }, 'платформа')),
        h('td', {}, r.pc_name || ''),
        h('td', { class: 'wrap muted' }, auditDetails(r.details)))))));
  }

  load();
  return {};
}
