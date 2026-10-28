// Управление парком ПК: подключение новых ПК по коду и команды (перезагрузить, выключить) одному ПК или сразу многим.
import { h, setChildren, badge, openModal, confirmDialog, toast, field, copyText } from '../dom.js';
import { get, post, del } from '../api.js';
import { fmtDateTime } from '../format.js';

const DAYS = [[1, 'Сутки'], [7, 'Неделя'], [30, 'Месяц'], [90, 'Три месяца']];
const CODE_STATUS = { active: ['действует', 'live'], used_up: ['ПК набрано', ''], expired: ['истёк', 'warn'], revoked: ['отозван', 'danger'] };
const COMMAND_NAME = { reboot: 'Перезагрузить', shutdown: 'Выключить' };

// ---------- подключение по коду ----------
/** Код подключения: один на все ПК зала. На каждом ПК при установке клиента вводится этот код, и ПК сам подключается к клубу и получает свой ключ. */
export async function enrollDialog(offset, done) {
  let zones = [];
  try { zones = await get('/api/admin/zones'); } catch (e) { toast(e.message, 'error'); return; }
  const zoneById = new Map(zones.map((z) => [z.id, z.name]));

  const label = h('input', { maxLength: 80, placeholder: 'например, Новые ПК VIP-зала' });
  const zone = h('select', {}, [h('option', { value: '' }, 'Без шаблона (общие тарифы)'), ...zones.map((z) => h('option', { value: z.id }, z.name))]);
  const days = h('select', {}, DAYS.map(([value, text]) => h('option', { value, selected: value === 7 }, text)));
  const uses = h('input', { type: 'number', min: 1, max: 1000, placeholder: 'без ограничения' });
  const error = h('div', { class: 'error-text', role: 'alert' });
  const fresh = h('div', {});
  const list = h('div', { class: 'pick-list' });

  async function loadCodes() {
    let rows = [];
    try { rows = await get('/api/admin/enroll-codes'); } catch (e) { setChildren(list, h('div', { class: 'small error-text' }, e.message)); return; }
    if (rows.length === 0) { setChildren(list, h('div', { class: 'small muted' }, 'Кодов пока нет.')); return; }
    setChildren(list, rows.map((c) => {
      const [text, kind] = CODE_STATUS[c.status] || [c.status, ''];
      const used = c.max_uses == null ? `подключено ПК: ${c.used_count}` : `подключено ${c.used_count} из ${c.max_uses}`;
      return h('div', { class: 'pick' },
        h('span', { class: 'grow' },
          h('b', {}, c.label || `Код ...${c.hint}`), ' ', badge(text, kind),
          h('div', { class: 'small muted' }, `${used}, до ${fmtDateTime(c.expires_at, offset).slice(0, 5)}${c.zone_id != null ? `, зал ${zoneById.get(c.zone_id) || '?'}` : ''}, оканчивается на ${c.hint}`)),
        c.status === 'active' ? h('button', { class: 'btn btn-sm', type: 'button', onclick: () => revoke(c) }, 'Отозвать') : null);
    }));
  }

  async function revoke(c) {
    const ok = await confirmDialog({ title: 'Отозвать код?', text: 'Новые ПК по нему больше не подключатся. Уже подключённые ПК продолжат работать.', okText: 'Отозвать', danger: true });
    if (!ok) return;
    try { await del(`/api/admin/enroll-codes/${c.id}`); toast('Код отозван', 'ok'); await loadCodes(); } catch (e) { toast(e.message, 'error'); }
  }

  const create = async () => {
    error.textContent = '';
    try {
      const body = { label: label.value.trim() || null, days: Number(days.value), max_uses: uses.value === '' ? null : Number(uses.value), zone_id: zone.value === '' ? null : Number(zone.value) };
      const made = await post('/api/admin/enroll-codes', body);
      showFresh(made.code);
      label.value = '';
      await loadCodes();
      if (done) done();
    } catch (e) { error.textContent = e.message; }
  };

  function showFresh(code) {
    const command = `.\\install.ps1 -Server ${location.origin} -EnrollCode ${code}`;
    setChildren(fresh, h('div', { class: 'warn-box', style: 'color:inherit;border-color:var(--accent)' },
      h('p', {}, h('b', {}, 'Код показывается один раз.'), ' Сохраните его: на сервере хранится только его отпечаток.'),
      h('div', { class: 'key-box mono' }, code),
      h('div', { class: 'row' },
        h('button', { class: 'btn btn-sm', type: 'button', onclick: async () => toast((await copyText(code)) ? 'Код скопирован' : 'Не удалось скопировать, выделите код вручную', 'ok') }, 'Скопировать код'),
        h('button', { class: 'btn btn-sm', type: 'button', onclick: async () => toast((await copyText(command)) ? 'Команда скопирована' : 'Не удалось скопировать', 'ok') }, 'Скопировать команду установки')),
      h('p', { class: 'small muted' }, 'На каждом ПК установщик запускается от имени администратора: ', h('span', { class: 'mono' }, command))));
  }

  const modal = openModal({
    title: 'Подключение ПК по коду', wide: true,
    content: [
      h('p', { class: 'small muted' }, 'Один код подходит для всех ПК зала. Вводить ключи вручную не нужно: ПК подключается сам, выбирает название по имени компьютера и встаёт в выбранный зал. Потом ПК остаётся расставить на карте.'),
      h('form', { onsubmit: (ev) => { ev.preventDefault(); create(); } },
        field('Название кода (для себя)', label), field('Зал для новых ПК', zone, 'Цены и характеристики берутся у зала'),
        h('div', { class: 'row' }, field('Срок действия', days), field('Сколько ПК можно подключить', uses)), error),
      fresh,
      h('h3', {}, 'Созданные коды'), list,
    ],
    actions: [h('button', { class: 'btn', type: 'button', onclick: () => modal.close() }, 'Закрыть'), h('button', { class: 'btn btn-primary', type: 'button', onclick: create }, 'Создать код')],
  });
  loadCodes();
}

// ---------- команды ----------
/** Как показать последнюю команду ПК одной строкой: «Перезагрузка в 14:05: выполнена (...)». */
export function commandLine(cmd, offset) {
  if (!cmd) return '';
  const at = fmtDateTime(cmd.created_at, offset).slice(6);
  const states = { pending: 'ждёт, когда ПК выйдет на связь', sent: 'отправлена на ПК, ждём подтверждения', done: 'выполнена', failed: 'не выполнена', cancelled: 'отменена' };
  const tail = cmd.result && (cmd.status === 'done' || cmd.status === 'failed' || cmd.status === 'cancelled') ? `: ${cmd.result}` : '';
  return `${cmd.label} в ${at}, ${states[cmd.status] || cmd.status}${tail}`;
}

export const isOpenCommand = (cmd) => !!cmd && (cmd.status === 'pending' || cmd.status === 'sent');

export async function cancelCommand(cmd, done) {
  try { await post(`/api/admin/commands/${cmd.id}/cancel`); toast('Команда отменена', 'ok'); if (done) done(); } catch (e) { toast(e.message, 'error'); }
}

/** Перезагрузить или выключить один ПК. С идущей сессией гостя команда прервёт игру, поэтому просим явное подтверждение. */
export async function commandFlow(pc, kind, done) {
  const reboot = kind === 'reboot';
  const playing = pc.mode === 'guest_session';
  const text = [
    playing ? `На ${pc.name} идёт сессия гостя: игра прервётся, время и деньги за остаток не вернутся автоматически. Лучше сначала остановить сессию.`
      : pc.cabinet_phone ? `За ${pc.name} сидит гость (сессия ещё не начата), его кабинет закроется.` : `${pc.name} выполнит команду через 20 секунд.`,
    reboot ? '' : 'Включить выключенный ПК из админки пока нельзя: его включают кнопкой на месте.',
    !pc.online ? `${pc.name} сейчас не на связи: команда будет ждать десять минут.` : '',
  ].filter(Boolean).join(' ');
  const ok = await confirmDialog({ title: `${reboot ? 'Перезагрузить' : 'Выключить'} ${pc.name}?`, text, okText: reboot ? 'Перезагрузить' : 'Выключить', danger: playing || !reboot });
  if (!ok) return;
  try {
    const r = await post(`/api/admin/pcs/${pc.id}/commands`, { kind, force: playing });
    toast(!r.created ? 'Такая команда уже ждёт выполнения' : r.online ? `Команда отправлена на ${pc.name}` : `${pc.name} не на связи: команда ждёт десять минут`, r.created && r.online ? 'ok' : 'error');
    if (done) done();
  } catch (e) { toast(e.message, 'error'); }
}

/** Одна команда нескольким ПК: например, в конце смены выключить все свободные. Занятые игрой по умолчанию не выбираются и массово не прерываются. */
export function bulkCommandDialog(pcs, done) {
  const picked = new Set();
  const kind = h('select', {}, Object.entries(COMMAND_NAME).map(([value, text]) => h('option', { value }, text)));
  const box = h('div', {});
  const result = h('div', {});
  const busyReason = (pc) => (pc.mode === 'guest_session' ? 'идёт сессия' : pc.cabinet_phone ? 'гость за ПК' : pc.booking && pc.booking.holding ? 'бронь' : pc.mode === 'staff_desktop' ? 'сотрудник' : '');
  const free = (pc) => pc.online && !busyReason(pc) && !pc.maintenance;
  for (const pc of pcs) if (free(pc)) picked.add(pc.id);

  function render() {
    const zones = [...new Set(pcs.map((p) => p.zone))];
    setChildren(box, zones.map((zone) => {
      const inZone = pcs.filter((p) => p.zone === zone);
      return h('section', {}, h('h3', {}, zone), h('div', { class: 'pick-list' }, inZone.map((pc) => {
        const reason = busyReason(pc);
        const locked = pc.mode === 'guest_session';                    // сессию массовой командой не прерываем
        const check = h('input', { type: 'checkbox', checked: picked.has(pc.id), disabled: locked });
        check.addEventListener('change', () => { if (check.checked) picked.add(pc.id); else picked.delete(pc.id); sync(); });
        return h('label', { class: 'pick', style: locked ? 'opacity:.6' : '' }, check, h('span', { class: 'grow' }, h('b', {}, pc.name)),
          reason ? badge(reason, locked ? 'warn' : '') : null, !pc.online ? badge('нет связи', 'danger') : null);
      })));
    }));
    sync();
  }

  const send = h('button', { class: 'btn btn-primary', type: 'button', onclick: () => run() }, 'Отправить');
  function sync() { send.textContent = `Отправить (${picked.size})`; send.disabled = picked.size === 0; }
  const choose = (test) => { picked.clear(); for (const pc of pcs) if (pc.mode !== 'guest_session' && test(pc)) picked.add(pc.id); render(); };

  async function run() {
    const ok = await confirmDialog({ title: `${COMMAND_NAME[kind.value]} ПК: ${picked.size}?`, text: `Команда уйдёт выбранным ПК, они выполнят её через 20 секунд. Занятые игрой ПК сервер пропустит и сообщит об этом.${kind.value === 'shutdown' ? ' Включить выключенный ПК из админки пока нельзя.' : ''}`, okText: COMMAND_NAME[kind.value], danger: true });
    if (!ok) return;
    send.disabled = true;
    try {
      const r = await post('/api/admin/commands', { pc_ids: [...picked], kind: kind.value, force: false });
      const name = new Map(pcs.map((p) => [p.id, p.name]));
      const refused = r.results.filter((x) => !x.ok);
      toast(r.refused ? `Поставлено: ${r.queued}, отказано: ${r.refused}` : `Команда поставлена ПК: ${r.queued}`, r.refused ? 'error' : 'ok');
      setChildren(result, refused.length ? h('div', { class: 'warn-box' }, h('b', {}, 'Команда не поставлена: '),
        refused.map((x) => h('div', { class: 'small' }, `${name.get(x.pc_id) || x.pc_id}: ${x.error}`))) : null);
      if (done) done();
    } catch (e) { toast(e.message, 'error'); } finally { send.disabled = picked.size === 0; }
  }

  const modal = openModal({
    title: 'Команда нескольким ПК', wide: true,
    content: [
      field('Что сделать', kind),
      h('div', { class: 'row', style: 'margin:8px 0' },
        h('button', { class: 'btn btn-sm', type: 'button', onclick: () => choose(free) }, 'Только свободные'),
        h('button', { class: 'btn btn-sm', type: 'button', onclick: () => choose(() => true) }, 'Все, кроме играющих'),
        h('button', { class: 'btn btn-sm', type: 'button', onclick: () => choose(() => false) }, 'Снять выбор')),
      box, result,
    ],
    actions: [h('button', { class: 'btn', type: 'button', onclick: () => modal.close() }, 'Закрыть'), send],
  });
  render();
}
