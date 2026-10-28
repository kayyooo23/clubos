import { h, setChildren, badge, openModal, confirmDialog, toast, field, emptyState } from '../dom.js';
import { get, post, patch, del } from '../api.js';
import { money, kindName, tariffDescribe, WEEKDAYS } from '../format.js';

const COMMON = 'common';                 // «общие тарифы клуба»: действуют в залах, у которых своих тарифов нет
const SPECS = [
  ['cpu', 'Процессор', 'например, Intel Core i7-14700K'],
  ['gpu', 'Видеокарта', 'например, RTX 4070 Super'],
  ['ram', 'Память', 'например, 32 ГБ'],
  ['monitor', 'Монитор', 'например, 27" 240 Гц'],
  ['peripherals', 'Периферия', 'мышь, клавиатура, наушники, кресло'],
  ['note', 'Заметка', 'для PS5: консоль, геймпады, телевизор'],
];

export function mount(root) {
  let zones = [];
  let tariffs = [];
  let selected = COMMON;
  let first = true;
  const chips = h('div', { class: 'legend' });
  const panel = h('div', {});
  root.append(
    h('div', { class: 'page-head' }, h('h1', {}, 'Залы и тарифы'), h('span', { class: 'spacer' }),
      h('button', { class: 'btn btn-primary', type: 'button', onclick: () => zoneModal() }, '+ Новый зал')),
    h('p', { class: 'muted small' }, 'Зал это шаблон для ПК: название, характеристики и свои тарифы. Добавляя ПК, вы выбираете зал и ничего не вводите заново. '
      + 'Если у зала есть свои тарифы, гости видят только их, иначе общие тарифы клуба. Отключённый тариф исчезает из выбора, а прошлые сессии по нему сохраняются в отчётах.'),
    chips, panel);

  async function load() {
    try {
      [zones, tariffs] = await Promise.all([get('/api/admin/zones'), get('/api/admin/tariffs')]);
    } catch (e) { toast(e.message, 'error'); return; }
    if (first && zones.length) selected = zones[0].id;
    first = false;
    if (selected !== COMMON && !zones.some((z) => z.id === selected)) selected = COMMON;
    render();
  }

  const common = () => tariffs.filter((t) => t.zone_id == null);
  const own = (zoneId) => tariffs.filter((t) => t.zone_id === zoneId);
  const priceLine = (list) => list.map((t) => `${t.name} ${money(t.price)}`).join(', ');

  function render() {
    setChildren(chips, [
      ...zones.map((z) => chip(z.id, `${z.name} ${z.pcs ?? 0} ПК`)),
      chip(COMMON, 'Общие тарифы'),
    ]);
    setChildren(panel, selected === COMMON ? commonPanel() : zonePanel(zones.find((z) => z.id === selected)));
  }

  function chip(key, text) {
    return h('button', { class: `btn btn-sm${selected === key ? ' active' : ''}`, type: 'button', onclick: () => { selected = key; render(); } }, text);
  }

  // ---------- таблица тарифов ----------
  function tariffTable(list) {
    if (list.length === 0) return null;
    return h('div', { class: 'table-wrap' }, h('table', {},
      h('thead', {}, h('tr', {}, ['Название', 'Тип', 'Условия', ''].map((t) => h('th', {}, t)))),
      h('tbody', {}, list.map((t) => h('tr', {},
        h('td', {}, h('b', {}, t.name)),
        h('td', {}, badge(kindName(t.kind), t.kind === 'night' ? 'accent' : '')),
        h('td', { class: 'wrap' }, tariffDescribe(t)),
        h('td', { class: 'right' },
          h('button', { class: 'btn btn-sm', type: 'button', onclick: () => tariffModal(zones.find((z) => z.id === t.zone_id) || null, t) }, 'Изменить'), ' ',
          h('button', { class: 'btn btn-danger btn-sm', type: 'button', onclick: () => disable(t) }, 'Отключить')))))));
  }

  // ---------- общие тарифы ----------
  function commonPanel() {
    const list = common();
    return h('section', {},
      h('div', { class: 'row', style: 'align-items:center;margin:14px 0 8px' }, h('h2', { style: 'margin:0' }, 'Общие тарифы'), h('span', { class: 'spacer' }),
        h('button', { class: 'btn btn-primary btn-sm', type: 'button', onclick: () => tariffModal(null) }, '+ Общий тариф')),
      h('p', { class: 'muted small' }, 'Действуют на ПК тех залов, у которых своих тарифов нет, и на ПК без зала.'),
      tariffTable(list) || emptyState('Общих тарифов нет. Залы без своих тарифов не смогут начать игру: добавьте тариф или заведите тарифы в самом зале.'));
  }

  // ---------- зал ----------
  function zonePanel(z) {
    const inputs = { name: h('input', { value: z.name, maxLength: 40, required: true }) };
    for (const [key, , placeholder] of SPECS) inputs[key] = h('input', { value: z[key] || '', maxLength: key === 'peripherals' || key === 'note' ? 200 : 100, placeholder });
    const error = h('div', { class: 'error-text', role: 'alert' });

    async function save() {
      error.textContent = '';
      const body = { name: inputs.name.value.trim() };
      if (!body.name) { error.textContent = 'Введите название зала'; return; }
      for (const [key] of SPECS) body[key] = inputs[key].value.trim();
      try { await patch(`/api/admin/zones/${z.id}`, body); toast('Зал сохранён', 'ok'); await load(); } catch (e) { error.textContent = e.message; }
    }

    async function remove() {
      const ok = await confirmDialog({ title: `Удалить зал «${z.name}»?`, text: 'Тарифы зала будут отключены. Зал с ПК удалить нельзя: сначала переведите их в другой зал.', okText: 'Удалить', danger: true });
      if (!ok) return;
      try { await del(`/api/admin/zones/${z.id}`); toast('Зал удалён', 'ok'); selected = COMMON; await load(); } catch (e) { toast(e.message, 'error'); }
    }

    const list = own(z.id);
    const shared = common();
    return h('section', {},
      h('div', { class: 'row', style: 'align-items:center;margin:14px 0 8px' }, h('h2', { style: 'margin:0' }, z.name), h('span', { class: 'muted small' }, `${z.pcs ?? 0} ПК`)),
      h('h3', {}, 'Характеристики'),
      h('p', { class: 'muted small' }, 'Что за ПК в этом зале. Показывается в панели ПК на карте; все поля необязательные.'),
      h('div', { class: 'row grow' }, field('Название зала', inputs.name), field('Процессор', inputs.cpu), field('Видеокарта', inputs.gpu)),
      h('div', { class: 'row grow' }, field('Память', inputs.ram), field('Монитор', inputs.monitor)),
      h('div', { class: 'row grow' }, field('Периферия', inputs.peripherals), field('Заметка', inputs.note)),
      error,
      h('div', { class: 'actions' },
        h('button', { class: 'btn btn-primary btn-sm', type: 'button', onclick: save }, 'Сохранить зал'),
        h('button', { class: 'btn btn-danger btn-sm', type: 'button', onclick: remove }, 'Удалить зал')),
      h('div', { class: 'row', style: 'align-items:center;margin:22px 0 8px' }, h('h3', { style: 'margin:0' }, 'Тарифы зала'), h('span', { class: 'spacer' }),
        h('button', { class: 'btn btn-primary btn-sm', type: 'button', onclick: () => tariffModal(z) }, '+ Тариф зала')),
      list.length
        ? tariffTable(list)
        : h('div', {},
          h('p', { class: 'muted small' }, shared.length
            ? `Своих тарифов у зала нет, гости видят общие: ${priceLine(shared)}. Заведите свои тарифы или скопируйте готовые и поправьте цены.`
            : 'Тарифов нет, гости не смогут начать игру. Заведите тарифы зала или скопируйте их из другого зала.'),
          copyBox(z)));
  }

  function copyBox(z) {
    const sources = [h('option', { value: '' }, `Общие тарифы клуба${common().length ? '' : ' (их нет)'}`),
      ...zones.filter((o) => o.id !== z.id && own(o.id).length).map((o) => h('option', { value: o.id }, `Зал «${o.name}»`))];
    const from = h('select', {}, sources);
    return h('div', { class: 'row', style: 'align-items:flex-end;gap:10px;flex-wrap:wrap' }, field('Скопировать тарифы из', from),
      h('button', { class: 'btn btn-sm', type: 'button', onclick: async () => {
        try {
          const r = await post(`/api/admin/zones/${z.id}/copy-tariffs`, { from_zone_id: from.value === '' ? null : Number(from.value) });
          toast(`Скопировано тарифов: ${r.copied}. Поправьте цены под этот зал`, 'ok');
          await load();
        } catch (e) { toast(e.message, 'error'); }
      } }, 'Скопировать'));
  }

  async function disable(t) {
    const ok = await confirmDialog({ title: `Отключить тариф «${t.name}»?`, text: 'Гости больше не увидят его при выборе. Уже идущие сессии не затрагиваются.', okText: 'Отключить', danger: true });
    if (!ok) return;
    try { await del(`/api/admin/tariffs/${t.id}`); toast('Тариф отключён', 'ok'); await load(); } catch (e) { toast(e.message, 'error'); }
  }

  // ---------- новый зал ----------
  function zoneModal() {
    const name = h('input', { maxLength: 40, placeholder: 'например, VIP, Premium, PS5 или Duo', required: true });
    const withTariffs = zones.filter((z) => own(z.id).length);
    const from = h('select', {}, [h('option', { value: 'none' }, 'Не копировать, заведу сам'),
      ...(common().length ? [h('option', { value: 'common' }, 'Общие тарифы клуба')] : []),
      ...withTariffs.map((z) => h('option', { value: z.id }, `Зал «${z.name}»`))]);
    const error = h('div', { class: 'error-text', role: 'alert' });

    async function save() {
      error.textContent = '';
      const value = name.value.trim();
      if (!value) { error.textContent = 'Введите название зала'; return; }
      try {
        const z = await post('/api/admin/zones', { name: value });
        if (from.value !== 'none') await post(`/api/admin/zones/${z.id}/copy-tariffs`, { from_zone_id: from.value === 'common' ? null : Number(from.value) });
        modal.close();
        selected = z.id;
        toast(`Зал «${value}» добавлен${from.value !== 'none' ? ': тарифы скопированы, поправьте цены' : ''}`, 'ok');
        await load();
      } catch (e) { error.textContent = e.message; }
    }

    const modal = openModal({
      title: 'Новый зал',
      content: h('form', { onsubmit: (ev) => { ev.preventDefault(); save(); } },
        field('Название', name),
        field('Тарифы нового зала', from, 'Скопируйте тарифы другого зала и потом поправьте цены: так не нужно вводить всё заново'), error),
      actions: [h('button', { class: 'btn', type: 'button', onclick: () => modal.close() }, 'Отмена'),
        h('button', { class: 'btn btn-primary', type: 'button', onclick: save }, 'Добавить зал')],
    });
    name.focus();
  }

  // ---------- новый тариф ----------
  /** zone: зал тарифа (null: общий). existing: меняем этот тариф (по сути новая версия: см. replace на сервере). */
  function tariffModal(zone, existing = null) {
    const name = h('input', { maxLength: 80, placeholder: 'например, «3 часа» или «Ночь»', required: true, value: existing ? existing.name : '' });
    const kind = h('select', {}, ['hourly', 'package', 'night'].map((k) => h('option', { value: k }, kindName(k))));
    if (existing) kind.value = existing.kind;
    const price = h('input', { type: 'number', min: 1, step: 1, inputMode: 'numeric', required: true, value: existing ? (existing.base_price ?? existing.price) : '' });
    const dayInputs = WEEKDAYS.map((_, i) => h('input', { type: 'number', min: 1, step: 1, inputMode: 'numeric', 'aria-label': `Цена, ${WEEKDAYS[i]}`,
      value: existing && existing.day_prices && existing.day_prices[i] ? existing.day_prices[i] : '' }));
    const dayGrid = h('div', { style: 'display:grid;grid-template-columns:repeat(7,1fr);gap:8px' },
      WEEKDAYS.map((d, i) => h('label', { class: 'field' }, h('span', {}, d), dayInputs[i])));
    const dayBox = h('div', {}, h('div', { class: 'small', style: 'margin:10px 0 4px' }, 'Цены по дням недели'), dayGrid,
      h('p', { class: 'small muted' }, 'Пусто: в этот день действует обычная цена. Цена выбирается по дню начала сессии и не меняется до её конца, даже через полночь.'));
    const minutes = h('input', { type: 'number', min: 1, step: 1, inputMode: 'numeric', placeholder: 'например, 180', value: existing && existing.package_minutes ? existing.package_minutes : '' });
    const hours = (value) => h('select', {}, Array.from({ length: 24 }, (_, i) => h('option', { value: i, selected: i === value }, `${String(i).padStart(2, '0')}:00`)));
    const start = hours(existing && existing.night_start != null ? existing.night_start : 23);
    const end = hours(existing && existing.night_end != null ? existing.night_end : 8);
    const priceLabel = h('span', {}, 'Цена за час, ₽');
    const minutesField = field('Длительность пакета, минут', minutes, '180 минут = 3 часа');
    const nightField = h('div', { class: 'row grow' }, field('Ночь начинается', start), field('Ночь заканчивается', end));
    const error = h('div', { class: 'error-text', role: 'alert' });
    const explain = h('p', { class: 'small muted' });

    function sync() {
      minutesField.hidden = kind.value !== 'package';
      nightField.hidden = kind.value !== 'night';
      priceLabel.textContent = kind.value === 'hourly' ? 'Обычная цена за час, ₽' : kind.value === 'package' ? 'Обычная цена пакета, ₽' : 'Обычная цена за всю ночь, ₽';
      explain.textContent = {
        hourly: 'Гость платит за фактическое время, с баланса списывается поминутно. Играть можно, пока хватает денег.',
        package: 'Фиксированная цена за пакет часов: списывается сразу при старте, при досрочной остановке не возвращается.',
        night: 'Фиксированная цена за ночь: стартовать можно только внутри ночного окна, сессия кончается с его концом (время клуба).',
      }[kind.value];
    }
    kind.addEventListener('change', sync);
    sync();

    async function save() {
      error.textContent = '';
      const body = { name: name.value.trim(), kind: kind.value, price: Number(price.value) };
      if (zone && !existing) body.zone_id = zone.id;
      if (!body.name) { error.textContent = 'Введите название'; return; }
      if (!Number.isInteger(body.price) || body.price <= 0) { error.textContent = 'Цена: целое число рублей больше нуля'; return; }
      const dayPrices = {};
      for (let i = 0; i < 7; i++) {
        const raw = dayInputs[i].value.trim();
        if (raw === '') continue;
        const value = Number(raw);
        if (!Number.isInteger(value) || value <= 0) { error.textContent = `Цена на ${WEEKDAYS[i]}: целое число рублей больше нуля или пусто`; return; }
        dayPrices[i] = value;
      }
      body.day_prices = Object.keys(dayPrices).length ? dayPrices : null;
      if (kind.value === 'package') {
        body.package_minutes = Number(minutes.value);
        if (!Number.isInteger(body.package_minutes) || body.package_minutes <= 0) { error.textContent = 'Укажите длительность пакета в минутах'; return; }
      }
      if (kind.value === 'night') {
        body.night_start = Number(start.value);
        body.night_end = Number(end.value);
        if (body.night_start === body.night_end) { error.textContent = 'Начало и конец ночи должны отличаться'; return; }
      }
      try {
        await post(existing ? `/api/admin/tariffs/${existing.id}/replace` : '/api/admin/tariffs', body);
        modal.close();
        toast(existing ? `Тариф «${body.name}» изменён (${money(body.price)})` : `Тариф «${body.name}» добавлен (${money(body.price)})`, 'ok');
        await load();
      } catch (e) { error.textContent = e.message; }
    }

    const modal = openModal({
      title: existing ? `Изменить тариф «${existing.name}»` : zone ? `Новый тариф зала «${zone.name}»` : 'Новый общий тариф',
      content: h('form', { onsubmit: (ev) => { ev.preventDefault(); save(); } },
        existing ? h('p', { class: 'small muted' }, 'Изменение создаёт новую версию тарифа: идущие сессии доработают по прежним условиям, а отчёты сохранятся.') : null,
        field('Название', name), field('Тип', kind), explain,
        h('label', { class: 'field' }, priceLabel, price), dayBox, minutesField, nightField, error),
      actions: [h('button', { class: 'btn', type: 'button', onclick: () => modal.close() }, 'Отмена'),
        h('button', { class: 'btn btn-primary', type: 'button', onclick: save }, existing ? 'Сохранить' : 'Добавить')],
    });
  }

  load();
}
