// «Выбрать на ПК»: список программ, которые служба нашла на компьютере клуба (ярлыки в меню «Пуск»). Из браузера полный путь к файлу узнать нельзя
// (он отдаёт только имя), поэтому список приходит от самого ПК, и выбранный путь заведомо существует там, где программу будут запускать.
import { h, setChildren, openModal, toast, emptyState } from '../dom.js';
import { get, post } from '../api.js';
import { fmtDateTime } from '../format.js';

const POLL_MS = 2000;
const WAIT_MS = 30000;

/** onPick({ name, path, icon }) вызывается, когда администратор выбрал программу. offset: часовой пояс клуба для подписи времени. */
export async function pickProgram({ onPick, offset = 3 }) {
  let pcs = [];
  try { pcs = await get('/api/admin/pcs'); } catch (e) { toast(e.message, 'error'); return; }
  if (pcs.length === 0) { toast('Сначала добавьте ПК: список программ приходит от него', 'error'); return; }

  const first = pcs.find((p) => p.online) || pcs[0];
  const pc = h('select', {}, pcs.map((p) => h('option', { value: p.id, selected: p.id === first.id }, `${p.name}${p.online ? '' : ' (не на связи)'}`)));
  const search = h('input', { type: 'search', autocomplete: 'off', placeholder: 'Поиск по названию' });
  const status = h('div', { class: 'small muted' });
  const listBox = h('div', { class: 'pick-list program-list' });
  const rescan = h('button', { class: 'btn btn-sm', type: 'button', title: 'Попросить ПК заново найти установленные программы', onclick: () => ask() }, 'Обновить список');

  let data = null;
  let timer = null;
  let version = 0;                              // защита от «старый ответ перекрыл новый» при смене ПК

  let waitId = 0;                               // номер текущего ожидания ответа ПК: смена ПК или закрытие окна отменяют его
  function stopPolling() { clearTimeout(timer); timer = null; waitId += 1; }

  /** Иконка программы (PNG, который прислал сам ПК); без неё серый квадрат, чтобы строки не прыгали. */
  function iconBox(p) {
    return h('span', { class: 'program-icon', 'aria-hidden': 'true' }, p.icon ? h('img', { src: `data:image/png;base64,${p.icon}`, alt: '' }) : '');
  }

  function render() {
    if (!data) { setChildren(listBox); return; }
    const q = search.value.trim().toLowerCase();
    const items = data.programs.filter((p) => !q || p.name.toLowerCase().includes(q) || p.path.toLowerCase().includes(q));
    if (data.programs.length === 0) {
      setChildren(listBox, emptyState(data.reported_at ? 'На ПК не нашлось подходящих программ' : 'ПК ещё не присылал список. Нажмите «Обновить список» и подождите несколько секунд.'));
    } else if (items.length === 0) {
      setChildren(listBox, emptyState('Ничего не найдено'));
    } else {
      setChildren(listBox, items.map((p) => h('button', { class: 'pick', type: 'button', onclick: () => choose(p) },
        iconBox(p), h('span', { class: 'grow' }, h('b', {}, p.name), h('div', { class: 'small muted mono' }, p.path)))));
    }
    status.textContent = data.pending ? 'Ждём ответ ПК…'
      : data.reported_at ? `Список от ${fmtDateTime(data.reported_at, offset)}, программ: ${data.programs.length}`
        : '';
  }

  /** light: пока ждём ответ ПК, иконки не качаем (список опрашивается каждые 2 секунды); когда ответ пришёл, берём список целиком. */
  async function load(light = false) {
    const mine = ++version;
    try {
      const res = await get(`/api/admin/pcs/${pc.value}/programs${light ? '?icons=false' : ''}`);
      if (mine !== version) return null;
      data = res;
      render();
      return res;
    } catch (e) { status.textContent = e.message; return null; }
  }

  async function ask() {
    stopPolling();
    const mine = waitId;
    rescan.disabled = true;
    try {
      const res = await post(`/api/admin/pcs/${pc.value}/programs/rescan`);
      if (mine !== waitId) return;
      data = res;
      render();
    } catch (e) { toast(e.message, 'error'); rescan.disabled = false; return; }
    const until = Date.now() + WAIT_MS;
    const tick = async () => {
      if (mine !== waitId) return;
      const res = await load(true);
      if (mine !== waitId) return;
      if (res && !res.pending) { rescan.disabled = false; await load(); return; }
      if (Date.now() > until) {
        rescan.disabled = false;
        status.textContent = 'ПК пока не ответил. Проверьте, что он включён и на связи, и нажмите ещё раз.';
        return;
      }
      timer = setTimeout(tick, POLL_MS);
    };
    timer = setTimeout(tick, POLL_MS);
  }

  function choose(program) {
    modal.close();
    onPick(program);
  }

  pc.addEventListener('change', () => { stopPolling(); rescan.disabled = false; data = null; render(); load(); });
  search.addEventListener('input', render);

  const modal = openModal({
    title: 'Выбрать программу на ПК', wide: true,
    content: [
      h('p', { class: 'small muted' }, 'Здесь программы, которые найдены на выбранном ПК клуба (ярлыки в меню «Пуск»). Установили новую программу: нажмите «Обновить список», выберите её, и путь подставится сам.'),
      h('div', { class: 'row' }, h('label', { class: 'field grow' }, h('span', {}, 'ПК'), pc), rescan),
      h('div', { class: 'field' }, search),
      status, listBox,
    ],
    actions: [h('button', { class: 'btn', type: 'button', onclick: () => modal.close() }, 'Закрыть')],
    onClose: () => { stopPolling(); version += 1; },
  });
  search.focus();
  load();
}
