import { h, setChildren, badge, openModal, confirmDialog, toast, field, emptyState, phoneInput } from '../dom.js';
import { get, post, patch } from '../api.js';
import { fmtDateTime, localInputToUtcIso, toLocalInput, minutesText, phonePretty, phoneDigits, phoneMask } from '../format.js';

const REFRESH_MS = 8000;
const DURATIONS = [30, 60, 90, 120, 180, 240, 360, 720];
const SHIFTS = [15, 30, 60, 120];       // быстрые сдвиги начала: «гость позвонил, что опаздывает»
const lengthOf = (b) => Math.round((new Date(`${b.ends_at}Z`) - new Date(`${b.starts_at}Z`)) / 60000);

function status(b) {
  if (b.status === 'used') return badge('гость пришёл', 'ok');
  if (b.status === 'cancelled') return badge('отменена');
  if (b.missed) return badge('не пришёл', 'warn');
  if (b.holding) return badge('забронирован', 'live');          // ПК уже удерживается за гостем
  return badge('забронирован', 'accent');                       // ПК начнёт удерживаться за 30 минут до начала
}

export function mount(root, ctx) {
  const offset = ctx.club.utc_offset;
  let all = false;
  const box = h('div', { class: 'table-wrap' });
  const toggle = h('button', { class: 'btn btn-sm', type: 'button', onclick: () => { all = !all; toggle.textContent = label(); load(); } }, '');
  const label = () => (all ? 'Только будущие' : 'Показать историю');
  toggle.textContent = label();
  root.append(
    h('div', { class: 'page-head' }, h('h1', {}, 'Бронирование'), h('span', { class: 'spacer' }), toggle,
      h('button', { class: 'btn btn-primary', type: 'button', onclick: () => addModal() }, '+ Новая бронь')),
    h('p', { class: 'muted small' }, 'Бронь держит ПК за гостем: за 30 минут до начала и 15 минут после начала сессию на этом ПК может начать только гость с этим номером. '
      + 'Не пришёл за 15 минут, ПК освобождается сам.'),
    box);

  async function load() {
    let list;
    try { list = await get(`/api/admin/bookings?scope=${all ? 'all' : 'upcoming'}`); } catch (e) { toast(e.message, 'error'); return; }
    if (list.length === 0) { setChildren(box, emptyState(all ? 'Броней пока не было' : 'Нет действующих броней')); return; }
    setChildren(box, h('table', {},
      h('thead', {}, h('tr', {}, ['ПК', 'Гость', 'Комментарий', 'Начало', 'Длительность', 'Статус', ''].map((t) => h('th', {}, t)))),
      h('tbody', {}, list.map((b) => h('tr', { class: b.status === 'cancelled' ? 'dim' : '' },
        h('td', {}, h('b', {}, b.pc_name)),
        h('td', { class: 'wrap' }, b.name ? `${b.name}, ` : '', phonePretty(b.phone)),
        h('td', { class: 'wrap' }, b.comment ? b.comment : h('span', { class: 'muted' }, 'нет')),
        h('td', {}, fmtDateTime(b.starts_at, offset)),
        h('td', { class: 'muted' }, minutesText(lengthOf(b))),
        h('td', {}, status(b)),
        h('td', { class: 'right' }, b.status === 'active' ? h('div', { class: 'row-actions' },
          h('button', { class: 'btn btn-sm', type: 'button', title: 'Что нужно подготовить к приходу гостя: мышь, клавиатура, наушники', onclick: () => commentModal(b) }, 'Комментарий'),
          h('button', { class: 'btn btn-sm', type: 'button', title: 'Сдвинуть время, сменить ПК, номер или длительность', onclick: () => editModal(b) }, 'Изменить'),
          h('button', { class: 'btn btn-danger btn-sm', type: 'button', onclick: () => cancel(b) }, 'Отменить')) : null))))));
  }

  async function cancel(b) {
    const ok = await confirmDialog({ title: `Отменить бронь ${b.pc_name}?`, text: `${phonePretty(b.phone)}, ${fmtDateTime(b.starts_at, offset)}. ПК освободится для других гостей.`, okText: 'Отменить бронь', danger: true });
    if (!ok) return;
    try { await post(`/api/admin/bookings/${b.id}/cancel`); toast('Бронь отменена', 'ok'); load(); } catch (e) { toast(e.message, 'error'); }
  }

  /** Быстрая заметка к брони: гость позвонил и просит конкретную мышь, клавиатуру или наушники. */
  function commentModal(b) {
    const comment = h('textarea', { maxLength: 200, rows: 4, placeholder: 'например, гость просит мышь Logitech и клавиатуру с подсветкой' });
    comment.value = b.comment || '';
    const error = h('div', { class: 'error-text', role: 'alert' });
    async function save() {
      error.textContent = '';
      try {
        await patch(`/api/admin/bookings/${b.id}`, { comment: comment.value.trim() });
        modal.close();
        toast('Комментарий сохранён', 'ok');
        load();
      } catch (e) { error.textContent = e.message; }
    }
    const modal = openModal({
      title: `Комментарий к брони: ${b.pc_name}`,
      content: h('form', { onsubmit: (ev) => { ev.preventDefault(); save(); } },
        h('div', { class: 'muted' }, `${b.name ? `${b.name}, ` : ''}${phonePretty(b.phone)}, ${fmtDateTime(b.starts_at, offset)}`),
        field('Комментарий (виден всем сотрудникам, на карте клуба тоже)', comment), error),
      actions: [h('button', { class: 'btn', type: 'button', onclick: () => modal.close() }, 'Отмена'),
        h('button', { class: 'btn btn-primary', type: 'button', onclick: save }, 'Сохранить')],
    });
    comment.focus();
  }

  /** Правка брони целиком: сдвинуть время (если гость не успевает), сменить длительность, ПК, номер, имя и комментарий. */
  async function editModal(b) {
    let pcs = [];
    try { pcs = await get('/api/admin/pcs'); } catch (e) { toast(e.message, 'error'); return; }
    const pc = h('select', {}, pcs.map((p) => h('option', { value: p.id, selected: p.id === b.pc_id }, `${p.name}${p.maintenance ? ' (обслуживание)' : ''}`)));
    const phone = phoneInput({ required: true, value: phoneMask(phoneDigits(b.phone)) });
    const name = h('input', { maxLength: 80, placeholder: 'необязательно', value: b.name || '' });
    const start = h('input', { type: 'datetime-local', required: true, value: toLocalInput(new Date(`${b.starts_at}Z`), offset) });
    const minutes = lengthOf(b);
    const lengths = DURATIONS.includes(minutes) ? DURATIONS : [...DURATIONS, minutes].sort((x, y) => x - y);
    const length = h('select', {}, lengths.map((m) => h('option', { value: m, selected: m === minutes }, minutesText(m))));
    const comment = h('textarea', { maxLength: 200, rows: 3, placeholder: 'необязательно' });
    comment.value = b.comment || '';
    const error = h('div', { class: 'error-text', role: 'alert' });

    const shift = (by) => {
      const iso = localInputToUtcIso(start.value, offset);
      if (iso) start.value = toLocalInput(new Date(new Date(iso).getTime() + by * 60000), offset);
    };
    const label = (m) => (m < 60 ? `+${m} мин` : `+${m / 60} ч`);

    async function save() {
      error.textContent = '';
      if (!phone.value.trim()) { error.textContent = 'Введите телефон гостя'; return; }
      const iso = localInputToUtcIso(start.value, offset);
      if (!iso) { error.textContent = 'Укажите время начала'; return; }
      try {
        await patch(`/api/admin/bookings/${b.id}`, { pc_id: Number(pc.value), phone: phone.value.trim(), name: name.value.trim(),
          comment: comment.value.trim(), starts_at: iso, minutes: Number(length.value) });
        modal.close();
        toast('Бронь изменена', 'ok');
        load();
      } catch (e) { error.textContent = e.message; }
    }

    const modal = openModal({
      title: `Изменить бронь: ${b.pc_name}`,
      content: h('form', { onsubmit: (ev) => { ev.preventDefault(); save(); } },
        field('ПК', pc), field('Телефон гостя', phone), field('Имя', name),
        h('div', { class: 'row grow' }, field('Начало (время клуба)', start), field('Длительность', length)),
        h('div', { class: 'quick' }, h('span', { class: 'small muted' }, 'Сдвинуть начало: '), SHIFTS.map((m) => h('button', { class: 'btn btn-sm', type: 'button', onclick: () => shift(m) }, label(m)))),
        field('Комментарий', comment), error),
      actions: [h('button', { class: 'btn', type: 'button', onclick: () => modal.close() }, 'Отмена'),
        h('button', { class: 'btn btn-primary', type: 'button', onclick: save }, 'Сохранить')],
    });
  }

  async function addModal() {
    let pcs = [];
    try { pcs = await get('/api/admin/pcs'); } catch (e) { toast(e.message, 'error'); return; }
    if (pcs.length === 0) { toast('Сначала добавьте ПК', 'error'); return; }
    const pc = h('select', {}, pcs.map((p) => h('option', { value: p.id }, `${p.name}${p.maintenance ? ' (обслуживание)' : ''}`)));
    const phone = phoneInput({ required: true });
    const name = h('input', { maxLength: 80, placeholder: 'необязательно' });
    const soon = new Date(Date.now() + 30 * 60000);
    const start = h('input', { type: 'datetime-local', required: true, value: toLocalInput(soon, offset) });
    const length = h('select', {}, DURATIONS.map((m) => h('option', { value: m, selected: m === 120 }, minutesText(m))));
    const comment = h('input', { maxLength: 200, placeholder: 'необязательно' });
    const error = h('div', { class: 'error-text', role: 'alert' });

    async function save() {
      error.textContent = '';
      if (!phone.value.trim()) { error.textContent = 'Введите телефон гостя'; return; }
      const iso = localInputToUtcIso(start.value, offset);
      if (!iso) { error.textContent = 'Укажите время начала'; return; }
      try {
        await post('/api/admin/bookings', { pc_id: Number(pc.value), phone: phone.value.trim(), name: name.value.trim() || null,
          comment: comment.value.trim() || null, starts_at: iso, minutes: Number(length.value) });
        modal.close();
        toast('Бронь сохранена', 'ok');
        load();
      } catch (e) { error.textContent = e.message; }
    }

    const modal = openModal({
      title: 'Новая бронь',
      content: h('form', { onsubmit: (ev) => { ev.preventDefault(); save(); } },
        field('ПК', pc), field('Телефон гостя', phone), field('Имя', name),
        h('div', { class: 'row grow' }, field('Начало (время клуба)', start), field('Длительность', length)),
        field('Комментарий', comment), error),
      actions: [h('button', { class: 'btn', type: 'button', onclick: () => modal.close() }, 'Отмена'),
        h('button', { class: 'btn btn-primary', type: 'button', onclick: save }, 'Забронировать')],
    });
  }

  const timer = setInterval(() => { if (!document.hidden) load(); }, REFRESH_MS);
  load();
  return { unmount() { clearInterval(timer); } };
}
