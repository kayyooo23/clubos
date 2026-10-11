import { h, setChildren, badge, toast, confirmDialog, openModal, field, emptyState } from '../dom.js';
import { get, post, patch } from '../api.js';
import { clock, phonePretty, money, fmtDateTime } from '../format.js';
import { startSessionDialog, addPcDialog, rotateKeyFlow } from './pcdialogs.js';
import { topupByPc } from './topup.js';

const MAX_COLS = 30, MAX_ROWS = 20;        // размер сетки такой же, как проверяет сервер
const MIN_COLS = 8, MIN_ROWS = 3;
const REFRESH_MS = 3000;
const NO_ZONE_NAME = 'Общий зал';

const STATES = {
  booked: { label: 'Забронирован', css: 'booked' },
  free: { label: 'Свободен', css: '' },
  playing: { label: 'Играет', css: 'playing' },
  staff: { label: 'Сотрудник', css: 'staff' },
  offline: { label: 'Нет связи', css: 'offline' },
  maint: { label: 'Обслуживание', css: 'maint' },
};

function stateOf(pc) {
  if (pc.mode === 'guest_session') return 'playing';
  if (pc.maintenance) return 'maint';
  if (pc.mode === 'staff_desktop') return 'staff';
  if (pc.booking && pc.booking.holding) return 'booked';
  if (!pc.online) return 'offline';
  return 'free';
}

export function mount(root, ctx) {
  const offset = ctx.club.utc_offset;
  let pcs = [];
  let fetchedAt = Date.now();
  let filter = 'all';
  let selectedId = null;
  let editing = false;
  let draft = new Map();                  // id -> { zone, x, y }, пока идёт перестановка
  let extraZones = [];                    // залы, созданные в режиме расстановки, но ещё пустые
  let dragId = null;
  const live = new Map();                 // id -> элемент с временем (обновляется раз в секунду)
  let drawerLive = null;                  // { el, left }: оставшееся время в панели выбранного ПК

  const legend = h('div', { class: 'legend' });
  const mapBox = h('div', {});
  const drawer = h('div', {});
  const editBtn = h('button', { class: 'btn', type: 'button', onclick: () => startEdit() }, 'Расставить ПК');
  const saveBtn = h('button', { class: 'btn btn-primary', type: 'button', onclick: () => saveEdit() }, 'Сохранить расстановку');
  const cancelBtn = h('button', { class: 'btn', type: 'button', onclick: () => cancelEdit() }, 'Отмена');
  const zoneBtn = h('button', { class: 'btn', type: 'button', onclick: () => addZoneModal() }, '+ Зал');
  const addBtn = h('button', { class: 'btn btn-primary', type: 'button', onclick: () => addPcDialog(load) }, '+ Добавить ПК');
  const head = h('div', { class: 'page-head' }, h('h1', {}, 'Карта клуба'), h('span', { class: 'spacer' }), addBtn, zoneBtn, editBtn, cancelBtn, saveBtn);
  root.append(head, legend, mapBox, drawer);
  syncButtons();

  function syncButtons() {
    editBtn.hidden = editing;
    addBtn.hidden = editing;
    saveBtn.hidden = cancelBtn.hidden = zoneBtn.hidden = !editing;
  }

  // ---------- данные ----------
  async function load() {
    try {
      pcs = await get('/api/admin/pcs');
      fetchedAt = Date.now();
    } catch (e) { toast(e.message, 'error'); return; }
    if (editing) return;                  // во время расстановки карту не перерисовываем
    renderAll();
  }

  const placeOf = (pc) => (editing && draft.has(pc.id) ? draft.get(pc.id) : { zone: pc.zone, x: pc.pos_x, y: pc.pos_y });

  // ---------- легенда и фильтры ----------
  function renderLegend() {
    const counts = { all: pcs.length, free: 0, booked: 0, playing: 0, staff: 0, offline: 0, maint: 0, config: 0 };
    for (const pc of pcs) { counts[stateOf(pc)] += 1; if (pc.hw_changed) counts.config += 1; }
    const items = [['all', 'Все'], ['free', 'Свободны'], ['booked', 'Бронь'], ['playing', 'Играют'], ['staff', 'Сотрудник'], ['offline', 'Нет связи'],
      ['maint', 'Обслуживание'], ['config', 'Конфигурация изменена']];
    setChildren(legend, items.filter(([key]) => key === 'all' || counts[key] > 0 || filter === key).map(([key, text]) =>
      h('button', { class: `btn btn-sm${filter === key ? ' active' : ''}`, type: 'button', onclick: () => { filter = key; renderAll(); } }, text, h('b', {}, String(counts[key])))));
  }

  const matches = (pc) => filter === 'all' || (filter === 'config' ? pc.hw_changed : stateOf(pc) === filter);

  // ---------- карта ----------
  function zoneNames() {
    const names = [];
    for (const pc of pcs) { const z = placeOf(pc).zone; if (!names.includes(z)) names.push(z); }
    for (const z of extraZones) if (!names.includes(z)) names.push(z);
    return names.length ? names : [NO_ZONE_NAME];
  }

  function tile(pc) {
    const st = stateOf(pc);
    const left = pc.session && pc.session.seconds_left != null ? clock(pc.session.seconds_left) : null;
    const bookedAt = pc.booking ? fmtDateTime(pc.booking.starts_at, offset).slice(6) : '';
    const sub = h('div', { class: 's' }, st === 'playing' ? (left ?? 'по факту') : st === 'booked' ? `Бронь ${bookedAt}` : STATES[st].label);
    if (st === 'playing' && pc.session && pc.session.seconds_left != null) live.set(pc.id, { el: sub, left: pc.session.seconds_left });
    const lines = [pc.name, STATES[st].label];
    if (pc.guest_phone) lines.push(phonePretty(pc.guest_phone));
    if (pc.session) lines.push(`${pc.session.tariff}, ${money(pc.session.cost)}`);
    if (pc.booking) lines.push(`Бронь: ${phonePretty(pc.booking.phone)}, ${fmtDateTime(pc.booking.starts_at, offset)}`);
    if (pc.booking && pc.booking.comment) lines.push(`К брони: ${pc.booking.comment}`);
    if (pc.note) lines.push(`Заметка: ${pc.note}`);
    if (pc.hw_changed) lines.push('Изменена конфигурация');
    const el = h('button', {
      class: ['tile', STATES[st].css, editing && 'drag', !matches(pc) && 'dim', pc.id === selectedId && !editing && 'selected'].filter(Boolean).join(' '),
      type: 'button', title: lines.join('\n'), draggable: editing,
      onclick: () => { if (!editing) { selectedId = pc.id; renderAll(); } },
      ondragstart: (ev) => { dragId = pc.id; ev.dataTransfer.effectAllowed = 'move'; ev.dataTransfer.setData('text/plain', String(pc.id)); },
      ondragend: () => { dragId = null; },
    }, h('div', { class: 'n' }, pc.name), sub, pc.hw_changed ? h('span', { class: 'flag', title: 'Изменена конфигурация' }, '!') : null);
    return el;
  }

  function dropOn(zone, x, y) {
    if (dragId == null) return;
    const mover = pcs.find((p) => p.id === dragId);
    const from = draft.get(dragId);
    const occupant = pcs.find((p) => p.id !== dragId && x != null && (() => { const o = draft.get(p.id); return o.zone === zone && o.x === x && o.y === y; })());
    if (occupant) draft.set(occupant.id, { zone: from.zone, x: from.x, y: from.y });   // занято: меняем местами
    draft.set(mover.id, { zone, x, y });
    dragId = null;
    renderMap();
  }

  function zoneBlock(zone) {
    const inZone = pcs.filter((p) => { const o = placeOf(p); return o.zone === zone && o.x != null; });
    const maxX = Math.max(MIN_COLS - 1, ...inZone.map((p) => placeOf(p).x)) + (editing ? 1 : 0);
    const maxY = Math.max(MIN_ROWS - 1, ...inZone.map((p) => placeOf(p).y)) + (editing ? 1 : 0);
    const cols = Math.min(maxX + 1, MAX_COLS), rows = Math.min(maxY + 1, MAX_ROWS);
    const grid = h('div', { class: 'map-grid' });
    grid.style.gridTemplateColumns = `repeat(${cols}, 124px)`;
    if (editing) {
      for (let y = 0; y < rows; y += 1) for (let x = 0; x < cols; x += 1) {
        const cell = h('div', { class: 'cell',
          ondragover: (ev) => { ev.preventDefault(); cell.classList.add('over'); },
          ondragleave: () => cell.classList.remove('over'),
          ondrop: (ev) => { ev.preventDefault(); dropOn(zone, x, y); } });
        cell.style.gridColumn = String(x + 1);
        cell.style.gridRow = String(y + 1);
        grid.append(cell);
      }
    }
    for (const pc of inZone) {
      const t = tile(pc);
      const o = placeOf(pc);
      t.style.gridColumn = String(o.x + 1);
      t.style.gridRow = String(o.y + 1);
      grid.append(t);
    }
    const removable = editing && inZone.length === 0 && pcs.every((p) => placeOf(p).zone !== zone);
    return h('section', { class: 'zone' },
      h('div', { class: 'zone-head' }, h('h2', {}, zone), h('span', { class: 'muted small' }, `${inZone.length} ПК`),
        removable ? h('button', { class: 'btn btn-sm', type: 'button', onclick: () => { extraZones = extraZones.filter((z) => z !== zone); renderMap(); } }, 'Убрать зал') : null),
      grid);
  }

  function renderMap() {
    live.clear();
    if (pcs.length === 0 && !editing) {
      setChildren(mapBox, emptyState('Компьютеров пока нет. Нажмите «Добавить ПК»: вы получите ключ для установки клиента.'));
      return;
    }
    const unplaced = pcs.filter((p) => placeOf(p).x == null);
    const blocks = zoneNames().map(zoneBlock);
    if (unplaced.length > 0 || editing) {
      const pile = h('div', { class: 'pile',
        ondragover: (ev) => { if (editing) ev.preventDefault(); },
        ondrop: (ev) => { ev.preventDefault(); if (dragId != null) { const o = draft.get(dragId); draft.set(dragId, { zone: o.zone, x: null, y: null }); dragId = null; renderMap(); } } },
      unplaced.map(tile));
      blocks.push(h('section', { class: 'zone' },
        h('div', { class: 'zone-head' }, h('h2', {}, 'Не размещены'),
          h('span', { class: 'muted small' }, editing ? 'Перетащите ПК в клетку зала' : 'Нажмите «Расставить ПК», чтобы разместить на карте')),
        pile));
    }
    setChildren(mapBox, blocks);
    updateLive();
  }

  function renderAll() {
    renderLegend();
    renderMap();
    renderDrawer();
  }

  function updateLive() {
    for (const [, item] of live) item.el.textContent = clock(item.left - (Date.now() - fetchedAt) / 1000);
    if (drawerLive) {
      const left = drawerLive.left - (Date.now() - fetchedAt) / 1000;
      drawerLive.el.textContent = clock(left);
      drawerLive.el.className = left < 60 ? 'danger' : left < 300 ? 'warn' : '';
    }
  }

  // ---------- режим расстановки ----------
  function startEdit() {
    editing = true;
    selectedId = null;
    draft = new Map(pcs.map((p) => [p.id, { zone: p.zone, x: p.pos_x, y: p.pos_y }]));
    extraZones = [];
    syncButtons();
    renderAll();
  }

  async function saveEdit() {
    const items = pcs.map((p) => ({ id: p.id, zone: draft.get(p.id).zone, pos_x: draft.get(p.id).x, pos_y: draft.get(p.id).y }));
    try {
      await post('/api/admin/pcs/layout', { items });
      toast('Расстановка сохранена', 'ok');
      editing = false;
      syncButtons();
      await load();
      renderAll();
    } catch (e) { toast(e.message, 'error'); }
  }

  function cancelEdit() {
    editing = false;
    extraZones = [];
    syncButtons();
    load().then(renderAll);
  }

  function addZoneModal() {
    const name = h('input', { maxLength: 40, required: true, placeholder: 'например, VIP' });
    const error = h('div', { class: 'error-text', role: 'alert' });
    const add = () => {
      const value = name.value.trim();
      if (!value) { error.textContent = 'Введите название зала'; return; }
      if (zoneNames().includes(value)) { error.textContent = 'Такой зал уже есть'; return; }
      extraZones.push(value);
      modal.close();
      renderMap();
    };
    const modal = openModal({
      title: 'Новый зал',
      content: h('form', { onsubmit: (ev) => { ev.preventDefault(); add(); } }, field('Название', name), error),
      actions: [h('button', { class: 'btn', type: 'button', onclick: () => modal.close() }, 'Отмена'),
        h('button', { class: 'btn btn-primary', type: 'button', onclick: add }, 'Добавить')],
    });
  }

  // ---------- панель ПК ----------
  function renderDrawer() {
    drawerLive = null;
    const pc = pcs.find((p) => p.id === selectedId);
    if (!pc || editing) { setChildren(drawer); return; }
    const st = stateOf(pc);
    const name = h('input', { value: pc.name, maxLength: 50 });
    const note = h('textarea', { maxLength: 200, placeholder: 'например, мышка залита, пока работает' }, '');
    note.value = pc.note || '';
    const kv = [];
    const s = pc.session;
    if (pc.booking) kv.push(['Бронь', `${phonePretty(pc.booking.phone)}${pc.booking.name ? `, ${pc.booking.name}` : ''}, с ${fmtDateTime(pc.booking.starts_at, offset)}`]);
    if (pc.booking && pc.booking.comment) kv.push(['К брони', pc.booking.comment]);
    if (st === 'playing' && s) {
      kv.push(['Гость', phonePretty(pc.guest_phone || '')], ['Тариф', s.tariff]);
      if (s.seconds_left != null) {
        // после конца пакета или ночи гость играет по часам: время тогда показывает, на сколько хватает его баланса
        const caption = s.overtime ? 'Хватит баланса на' : s.kind === 'hourly' ? 'Баланса хватит на' : 'Осталось';
        const timeEl = h('b', {});
        drawerLive = { el: timeEl, left: s.seconds_left };
        kv.push([caption, timeEl]);
      } else kv.push(['Осталось', 'по факту']);
      kv.push(['Стоимость', money(s.cost)], ['Начало', fmtDateTime(s.started_at, offset)]);
      if (s.overtime) kv.push(['Сейчас', 'тариф кончился, идёт почасовая оплата']);
    }
    const actions = [h('button', { class: 'btn btn-primary btn-sm', type: 'button', title: 'Принять наличные у гостя, который сидит за этим ПК',
      onclick: () => topupByPc(pc, load) }, 'Пополнить баланс')];
    if (st === 'playing' && s) actions.push(h('button', { class: 'btn btn-danger btn-sm', type: 'button', onclick: () => stop(pc) }, 'Остановить сессию'));
    else if (st !== 'staff' && st !== 'maint') actions.push(h('button', { class: 'btn btn-sm', type: 'button', onclick: () => startSessionDialog(pc, load) }, 'Запустить сессию'));
    if (st === 'staff') actions.push(h('button', { class: 'btn btn-sm', type: 'button', onclick: () => lock(pc) }, 'Заблокировать ПК'));
    actions.push(h('button', { class: 'btn btn-sm', type: 'button', disabled: st === 'playing',
      title: st === 'playing' ? 'Сначала остановите сессию' : 'На обслуживании гостей на ПК не пускают',
      onclick: () => save(pc, { maintenance: !pc.maintenance }, pc.maintenance ? 'ПК снова в работе' : 'ПК на обслуживании') },
    pc.maintenance ? 'Снять с обслуживания' : 'Поставить на обслуживание'));
    actions.push(h('button', { class: 'btn btn-ghost btn-sm', type: 'button', title: 'Выдать новый ключ для клиента на этом ПК', onclick: () => rotateKeyFlow(pc) }, 'Ключ ПК'));

    setChildren(drawer, h('aside', { class: 'drawer', 'aria-label': pc.name },
      h('div', { class: 'drawer-head' }, h('h2', {}, pc.name), badge(STATES[st].label, st === 'playing' ? 'live' : st === 'maint' ? 'warn' : ''),
        h('button', { class: 'icon-btn', type: 'button', 'aria-label': 'Закрыть', onclick: () => { selectedId = null; renderAll(); } }, '×')),
      h('div', { class: 'small muted' }, `${pc.zone}${pc.pos_x != null ? `, клетка ${pc.pos_x + 1} x ${pc.pos_y + 1}` : ', не размещён'}`),
      kv.length ? h('dl', { class: 'kv', style: 'margin-top:14px' }, kv.flatMap(([k, v]) => [h('dt', {}, k), h('dd', {}, v)])) : null,
      h('div', { class: 'actions' }, actions),
      pc.hw_changed ? h('div', { class: 'warn-box' }, 'Изменена конфигурация ПК. ',
        h('button', { class: 'btn btn-sm', type: 'button', onclick: () => ack(pc) }, 'Всё верно')) : null,
      h('h3', {}, 'Название и заметка'),
      field('Название', name), field('Заметка для сотрудников', note),
      h('div', { class: 'actions' }, h('button', { class: 'btn btn-primary btn-sm', type: 'button', onclick: () => save(pc, { name: name.value.trim(), note: note.value.trim() }, 'Сохранено') }, 'Сохранить')),
      h('h3', {}, 'Железо'),
      hardware(pc)));
    updateLive();                          // время в панели сразу, не через секунду
  }

  function hardware(pc) {
    const hw = pc.hw;
    if (!hw) return h('div', { class: 'muted small' }, 'Клиент на этом ПК ещё не присылал характеристики. Они приходят через минуту после запуска и потом раз в несколько часов.');
    const rows = [['Процессор', hw.cpu], ['Память', hw.ram_gb != null ? `${hw.ram_gb} ГБ` : null], ['Видеокарта', hw.gpu], ['Система', hw.os],
      ['Адрес', hw.ip], ['MAC', hw.mac], ['Отчёт', fmtDateTime(hw.reported_at, offset)]].filter(([, v]) => v);
    return h('div', {},
      h('dl', { class: 'kv' }, rows.flatMap(([k, v]) => [h('dt', {}, k), h('dd', {}, v)])),
      ...(hw.disks || []).map((d) => {
        const used = d.total_gb > 0 ? Math.round(((d.total_gb - d.free_gb) / d.total_gb) * 100) : 0;
        const bar = h('i', {});
        bar.style.width = `${used}%`;
        return h('div', { style: 'margin-top:10px' }, h('div', { class: 'small' }, `${d.name} свободно ${d.free_gb} из ${d.total_gb} ГБ`), h('div', { class: 'bar' }, bar));
      }));
  }

  // ---------- действия ----------
  async function save(pc, body, okText) {
    try {
      await patch(`/api/admin/pcs/${pc.id}`, body);
      toast(okText, 'ok');
      await load();
    } catch (e) { toast(e.message, 'error'); }
  }

  async function stop(pc) {
    const ok = await confirmDialog({ title: `Остановить сессию на ${pc.name}?`,
      text: `Гость ${phonePretty(pc.guest_phone || '')} будет отключён, ПК заблокируется. Пакет и ночной тариф не возвращаются.`, okText: 'Остановить', danger: true });
    if (!ok) return;
    try { await post(`/api/admin/sessions/${pc.session.id}/stop`); toast('Сессия остановлена', 'ok'); await load(); } catch (e) { toast(e.message, 'error'); }
  }

  async function lock(pc) {
    try { await post(`/api/admin/pcs/${pc.id}/lock`); toast('ПК заблокирован', 'ok'); await load(); } catch (e) { toast(e.message, 'error'); }
  }

  async function ack(pc) {
    try { await post(`/api/admin/pcs/${pc.id}/ack-hardware`); await load(); } catch (e) { toast(e.message, 'error'); }
  }

  const poll = setInterval(() => { if (!document.hidden && !drawerBusy()) load(); }, REFRESH_MS);
  const tick = setInterval(updateLive, 1000);
  /** Пока сотрудник печатает название или заметку, панель не перерисовываем: введённое пропало бы. */
  const drawerBusy = () => !!drawer.querySelector('input:focus, textarea:focus');
  load();
  return { unmount() { clearInterval(poll); clearInterval(tick); } };
}
