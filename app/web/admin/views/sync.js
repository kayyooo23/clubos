// Синхронизация игр: игру ставят или обновляют один раз на эталонном ПК, а система копирует её на остальные ПК клуба по локальной сети.
// Здесь администратор выбирает эталон, игры и получателей и смотрит, как идёт копирование на каждом ПК.
import { h, setChildren, badge, toast, confirmDialog, field, emptyState } from '../dom.js';
import { get, post } from '../api.js';
import { bytesText, fmtDateTime } from '../format.js';

const STATUS = {
  pending: ['ждёт', ''], scanning: ['считает файлы', 'accent'], waiting: ['ждёт', 'warn'], syncing: ['копирует', 'live'],
  ready: ['готов', 'ok'], failed: ['ошибка', 'danger'], cancelled: ['отменено', ''],
};
const JOB_STATUS = { running: ['идёт', 'live'], done: ['завершено', 'ok'], cancelled: ['отменено', ''] };
const REFRESH_MS = 3000;

export function mount(root, ctx) {
  const offset = ctx.club.utc_offset;
  let pcs = [];
  let games = null;                       // ответ /pcs/{id}/games для выбранного эталона
  let sourceId = null;
  const picked = new Set();               // выбранные пути игр
  const receivers = new Set();            // выбранные получатели; пусто и режим «все»: все остальные ПК
  let everyone = true;
  let mirror = false;
  let rescanTimer = null;

  const form = h('div', {});
  const jobsBox = h('div', {});
  root.append(
    h('div', { class: 'page-head' }, h('h1', {}, 'Синхронизация игр')),
    h('p', { class: 'muted small' }, 'Поставьте или обновите игру на одном ПК (эталоне), выберите её здесь, и она скопируется на остальные ПК по локальной сети. Копируются только изменившиеся части файлов. ',
      'ПК копируют, когда свободны (на экране блокировки, без гостя), а получившие игру ПК раздают её остальным, поэтому эталон не перегружается.'),
    form, h('h2', { style: 'margin-top:28px' }, 'Задания'), jobsBox);

  // ---------- форма ----------
  async function loadPcs() {
    try { pcs = await get('/api/admin/pcs'); } catch (e) { toast(e.message, 'error'); return; }
    if (sourceId == null) sourceId = (pcs.find((p) => p.online) || pcs[0] || {}).id ?? null;
    await loadGames();
  }

  async function loadGames() {
    games = null;
    if (sourceId != null) { try { games = await get(`/api/admin/pcs/${sourceId}/games`); } catch (e) { toast(e.message, 'error'); } }
    renderForm();
  }

  function stopRescan() { if (rescanTimer) { clearInterval(rescanTimer); rescanTimer = null; } }

  async function rescan() {
    try { games = await post(`/api/admin/pcs/${sourceId}/games/rescan`); } catch (e) { toast(e.message, 'error'); return; }
    renderForm();
    let tries = 0;
    stopRescan();
    rescanTimer = setInterval(async () => {
      tries += 1;
      try { games = await get(`/api/admin/pcs/${sourceId}/games`); } catch { return; }
      if (!games.pending || tries > 20) {
        stopRescan();
        if (games.pending) toast('ПК не ответил: проверьте, что он на связи и в настройках клиента задана игровая папка', 'error');
        renderForm();
      }
    }, 2000);
    toast('Запросили список игр у ПК, это займёт несколько секунд', 'ok');
  }

  function renderForm() {
    const source = pcs.find((p) => p.id === sourceId);
    const sourceSelect = h('select', {}, pcs.map((p) => h('option', { value: p.id, selected: p.id === sourceId }, `${p.name}${p.online ? '' : ' (нет связи)'}`)));
    sourceSelect.addEventListener('change', () => { sourceId = Number(sourceSelect.value); picked.clear(); receivers.delete(sourceId); stopRescan(); loadGames(); });

    const entries = games ? games.entries : [];
    let gamesBlock;
    if (!source) gamesBlock = emptyState('В клубе нет ПК');
    else if (games && games.pending) gamesBlock = h('div', { class: 'muted small' }, 'Ждём список игр от ПК...');
    else if (!games || games.reported_at == null) {
      gamesBlock = h('div', { class: 'muted small' }, 'ПК ещё не присылал список игр. Нажмите «Обновить список игр». Если список так и не приходит, на этом ПК в настройках клиента не задана игровая папка (games_root).');
    } else if (entries.length === 0) {
      gamesBlock = h('div', { class: 'muted small' }, `В игровой папке ${games.root || ''} нет игр.`);
    } else {
      gamesBlock = h('div', { class: 'pick-list' }, entries.map((e) => {
        const check = h('input', { type: 'checkbox', checked: picked.has(e.path) });
        check.addEventListener('change', () => { if (check.checked) picked.add(e.path); else picked.delete(e.path); syncStart(); });
        return h('label', { class: 'pick' }, check, h('span', { class: 'grow' }, h('b', {}, e.name), ' ', e.kind === 'steam' ? badge('Steam') : null,
          h('div', { class: 'small muted' }, `${bytesText(e.size_bytes)}, файлов: ${e.files}`)));
      }));
    }

    const others = pcs.filter((p) => p.id !== sourceId);
    const everyoneRadio = h('input', { type: 'radio', name: 'who', checked: everyone });
    const chosenRadio = h('input', { type: 'radio', name: 'who', checked: !everyone });
    everyoneRadio.addEventListener('change', () => { everyone = true; renderForm(); });
    chosenRadio.addEventListener('change', () => { everyone = false; renderForm(); });
    const targetList = everyone ? null : h('div', { class: 'pick-list' }, others.map((p) => {
      const check = h('input', { type: 'checkbox', checked: receivers.has(p.id) });
      check.addEventListener('change', () => { if (check.checked) receivers.add(p.id); else receivers.delete(p.id); syncStart(); });
      return h('label', { class: 'pick' }, check, h('span', { class: 'grow' }, h('b', {}, p.name)), p.online ? null : badge('нет связи', 'danger'));
    }));
    const mirrorBox = h('input', { type: 'checkbox', checked: mirror });
    mirrorBox.addEventListener('change', () => { mirror = mirrorBox.checked; });

    const start = h('button', { class: 'btn btn-primary', type: 'button', onclick: () => begin() }, 'Начать синхронизацию');
    function syncStart() { start.disabled = !canStart(); }
    const canStart = () => source && source.online && picked.size > 0 && (everyone ? others.some((p) => p.online) : receivers.size > 0);
    syncStart();

    setChildren(form, h('section', { class: 'card', style: 'padding:16px;border:2px solid var(--line, #333)' },
      field('Эталонный ПК (игры уже установлены на нём)', sourceSelect),
      h('div', { class: 'row', style: 'margin:10px 0 6px' }, h('b', {}, 'Игры'), h('span', { class: 'grow' }),
        h('button', { class: 'btn btn-sm', type: 'button', disabled: !source || !source.online, onclick: () => rescan(),
          title: 'Попросить ПК заново посмотреть игровую папку' }, 'Обновить список игр')),
      gamesBlock,
      h('div', { style: 'margin-top:14px' }, h('b', {}, 'Куда копировать')),
      h('label', { class: 'row', style: 'gap:8px;margin:6px 0' }, everyoneRadio, `На все остальные ПК, которые сейчас на связи (${others.filter((p) => p.online).length})`),
      h('label', { class: 'row', style: 'gap:8px;margin:6px 0' }, chosenRadio, 'Только на выбранные'),
      targetList,
      h('label', { class: 'row', style: 'gap:8px;margin:12px 0' }, mirrorBox, h('span', {}, 'Точное зеркало: на получателях удалить файлы выбранных игр, которых нет на эталоне')),
      h('div', { class: 'actions' }, start)));
  }

  async function begin() {
    const source = pcs.find((p) => p.id === sourceId);
    const targetIds = everyone ? null : [...receivers];
    const count = everyone ? pcs.filter((p) => p.id !== sourceId && p.online).length : receivers.size;
    const ok = await confirmDialog({
      title: 'Начать синхронизацию?',
      text: `С ${source.name} на ПК: ${count}. Игр выбрано: ${picked.size}. ПК, занятые гостями, начнут копирование, когда освободятся.${mirror ? ' Файлы выбранных игр, которых нет на эталоне, будут удалены на получателях.' : ''}`,
      okText: 'Начать', danger: mirror,
    });
    if (!ok) return;
    try {
      const job = await post('/api/admin/sync/jobs', { source_pc_id: sourceId, paths: [...picked], target_pc_ids: targetIds, delete_extra: mirror });
      toast(job.skipped_offline && job.skipped_offline.length ? `Синхронизация запущена. ПК без связи пропущены: ${job.skipped_offline.join(', ')}. Скопируйте на них позже` : 'Синхронизация запущена', 'ok');
      picked.clear();
      renderForm();
      loadJobs();
    } catch (e) { toast(e.message, 'error'); }
  }

  // ---------- задания ----------
  async function loadJobs() {
    let jobs;
    try { jobs = await get('/api/admin/sync/jobs'); } catch (e) { toast(e.message, 'error'); return; }
    if (jobs.length === 0) { setChildren(jobsBox, emptyState('Заданий пока нет')); return; }
    setChildren(jobsBox, jobs.map(jobCard));
  }

  function jobCard(job) {
    const [jobText, jobKind] = JOB_STATUS[job.status] || [job.status, ''];
    const s = job.summary;
    const line = `Готово ${s.ready} из ${s.total}${s.failed ? `, с ошибкой ${s.failed}` : ''}. Скачано ${bytesText(s.fetched)}${s.from_peers ? `, из них с других ПК ${bytesText(s.from_peers)}` : ''}`;
    const rows = job.targets.map((t) => {
      const [text, kind] = STATUS[t.status] || [t.status, ''];
      const bar = h('i', {});
      bar.style.width = `${t.percent}%`;
      return h('tr', {},
        h('td', {}, h('b', {}, t.pc_name), t.role === 'source' ? h('span', { class: 'muted small' }, ' эталон') : null),
        h('td', {}, badge(text, kind)),
        h('td', { style: 'min-width:140px' }, t.role === 'source' && t.status === 'ready' ? h('span', { class: 'muted small' }, `файлов: ${t.files_total}`)
          : h('div', {}, h('div', { class: 'bar' }, bar), h('div', { class: 'small muted' }, `${t.percent}%${t.bytes_total ? `, ${bytesText(t.bytes_done)} из ${bytesText(t.bytes_total)}` : ''}`))),
        h('td', { class: 'small' }, t.role === 'target' && t.bytes_fetched ? `скачано ${bytesText(t.bytes_fetched)}` : ''),
        h('td', { class: 'small muted' }, t.message || ''));
    });
    return h('section', { style: 'border:2px solid var(--line, #333);padding:14px;margin-bottom:14px' },
      h('div', { class: 'row' }, h('b', {}, job.paths.map((p) => p.split('/').pop()).join(', ')), badge(jobText, jobKind), h('span', { class: 'grow' }),
        job.status === 'running' ? h('button', { class: 'btn btn-sm', type: 'button', onclick: () => cancel(job) }, 'Отменить') : null),
      h('div', { class: 'small muted' }, `С ${job.source_name}, начато ${fmtDateTime(job.created_at, offset)}${job.manifest_files != null ? `, файлов ${job.manifest_files}, ${bytesText(job.manifest_bytes)}` : ''}${job.delete_extra ? ', точное зеркало' : ''}`),
      h('div', { style: 'margin:6px 0' }, line),
      h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['ПК', 'Состояние', 'Ход', 'Сеть', 'Комментарий'].map((t) => h('th', {}, t)))), h('tbody', {}, rows))));
  }

  async function cancel(job) {
    const ok = await confirmDialog({ title: 'Отменить синхронизацию?', text: 'Копирование остановится на всех ПК. Уже скопированные файлы останутся, недокачанные файлы в играх не появятся.', okText: 'Отменить', danger: true });
    if (!ok) return;
    try { await post(`/api/admin/sync/jobs/${job.id}/cancel`); toast('Синхронизация отменена', 'ok'); loadJobs(); } catch (e) { toast(e.message, 'error'); }
  }

  const poll = setInterval(() => { if (!document.hidden) loadJobs(); }, REFRESH_MS);
  loadPcs();
  loadJobs();
  return { unmount() { clearInterval(poll); stopRescan(); } };
}
