import { h, setChildren, toast, emptyState } from '../dom.js';
import { get } from '../api.js';
import { money, phonePretty, fmtDateTime, startOfClubDay, toLocalInput, localInputToUtcIso } from '../format.js';

const DAY = 24 * 3600 * 1000;

export function mount(root, ctx) {
  const offset = ctx.club.utc_offset;
  const sign = offset >= 0 ? '+' : '−';
  const out = h('div', {});
  let activePreset = 'today';

  const presets = [
    { id: 'today', title: 'Сегодня', range: () => ({ since: startOfClubDay(offset, 0), until: null }) },
    { id: 'yesterday', title: 'Вчера', range: () => ({ since: startOfClubDay(offset, 1), until: new Date(startOfClubDay(offset, 0).getTime() - 1) }) },
    { id: 'week', title: '7 дней', range: () => ({ since: startOfClubDay(offset, 6), until: null }) },
    { id: 'month', title: '30 дней', range: () => ({ since: startOfClubDay(offset, 29), until: null }) },
  ];
  const buttons = new Map();
  const presetBar = h('div', { class: 'presets' }, presets.map((p) => {
    const b = h('button', { class: 'btn btn-sm', type: 'button', onclick: () => choose(p.id) }, p.title);
    buttons.set(p.id, b);
    return b;
  }));

  const from = h('input', { type: 'datetime-local', 'aria-label': 'Начало периода' });
  const to = h('input', { type: 'datetime-local', 'aria-label': 'Конец периода' });
  from.value = toLocalInput(startOfClubDay(offset, 0), offset);
  to.value = toLocalInput(new Date(), offset);

  root.append(
    h('div', { class: 'page-head no-print' }, h('h1', {}, 'Отчёт'), presetBar, h('span', { class: 'spacer' }),
      h('button', { class: 'btn btn-sm', type: 'button', onclick: () => window.print() }, 'Распечатать')),
    h('form', { class: 'row no-print', onsubmit: (ev) => { ev.preventDefault(); custom(); } },
      h('span', { class: 'muted small' }, `Свой период (время клуба, UTC${sign}${Math.abs(offset)}):`),
      h('div', { class: 'dt-box' }, from), h('span', { class: 'muted' }, 'по'), h('div', { class: 'dt-box' }, to),
      h('button', { class: 'btn btn-sm', type: 'submit' }, 'Показать')),
    out);

  function mark(id) {
    activePreset = id;
    for (const [key, b] of buttons) b.classList.toggle('active', key === id);
  }

  function choose(id) {
    mark(id);
    const range = presets.find((p) => p.id === id).range();
    load(range.since, range.until);
  }

  function custom() {
    mark('');
    const since = localInputToUtcIso(from.value, offset);
    const until = localInputToUtcIso(to.value, offset);
    if (!since || !until) { toast('Укажите начало и конец периода', 'error'); return; }
    load(new Date(since), new Date(until));
  }

  async function load(since, until) {
    try {
      const qs = `since=${encodeURIComponent(since.toISOString())}${until ? `&until=${encodeURIComponent(until.toISOString())}` : ''}`;
      const data = await get(`/api/admin/report/summary?${qs}`);
      render(data, since, until);
    } catch (e) { toast(e.message, 'error'); }
  }

  const stat = (label, value, hint, cls = '') => h('div', { class: 'stat' }, h('div', { class: 'label' }, label), h('div', { class: `value ${cls}` }, value), hint ? h('div', { class: 'hint' }, hint) : null);

  function render(d, since, until) {
    const until2 = until || new Date(d.until + 'Z');
    setChildren(out,
      h('p', { class: 'muted' }, `Период: с ${fmtDateTime(since.toISOString(), offset)} по ${fmtDateTime(until2.toISOString(), offset)} (время клуба)`),
      h('div', { class: 'stats' },
        stat('Выручка за сессии', money(d.revenue), 'по сессиям, завершённым за период', 'ok'),
        stat('Завершённых сессий', d.sessions),
        stat('Принято наличными', money(d.cash_in), 'пополнения баланса за период: столько должно быть в кассе'),
        stat('Корректировки', `${d.adjustments > 0 ? '+' : ''}${money(d.adjustments)}`, `операций: ${d.adjustments_count}. Это не деньги в кассе, а исправления баланса`)),
      h('h2', { class: 'section-title' }, 'По тарифам'),
      d.by_tariff.length === 0 ? h('div', { class: 'table-wrap' }, emptyState('За этот период сессий не было')) :
        h('div', { class: 'table-wrap' }, h('table', {},
          h('thead', {}, h('tr', {}, h('th', {}, 'Тариф'), h('th', { class: 'right' }, 'Сессий'), h('th', { class: 'right' }, 'Выручка'))),
          h('tbody', {}, d.by_tariff.map((t) => h('tr', {}, h('td', {}, t.tariff), h('td', { class: 'right' }, t.sessions), h('td', { class: 'right' }, money(t.revenue))))))),
      h('h2', { class: 'section-title' }, 'Наличные по сотрудникам'),
      d.cash_by_staff.length === 0 ? h('div', { class: 'table-wrap' }, emptyState('Пополнений наличными не было')) :
        h('div', { class: 'table-wrap' }, h('table', {},
          h('thead', {}, h('tr', {}, h('th', {}, 'Сотрудник'), h('th', { class: 'right' }, 'Операций'), h('th', { class: 'right' }, 'Принято'))),
          h('tbody', {}, d.cash_by_staff.map((s) => h('tr', {}, h('td', {}, phonePretty(s.staff)), h('td', { class: 'right' }, s.operations), h('td', { class: 'right' }, money(s.amount))))))),
      h('p', { class: 'small muted' }, 'Пакеты и ночные тарифы оплачиваются при старте, но попадают в выручку в день завершения сессии; почасовые по фактической стоимости при завершении.'));
  }

  choose('today');
  return {};
}
