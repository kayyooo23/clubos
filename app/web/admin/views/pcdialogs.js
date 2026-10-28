// Окна для работы с одним ПК: добавить ПК, выдать ключ, запустить сессию за гостя. Раньше жили на вкладке «Компьютеры», теперь всё делается на карте клуба.
import { h, setChildren, badge, openModal, confirmDialog, toast, field, copyText, digitsInput, phoneInput } from '../dom.js';
import { get, post } from '../api.js';
import { money, phonePretty, tariffDescribe, minBalance, kindName, minutesText } from '../format.js';

const HOLD_LENGTHS = [60, 120, 180, 240, 360];

/** Ключ ПК показывается один раз: его нужно прописать при установке клиента. */
export function keyDialog(title, key) {
  const copy = h('button', { class: 'btn btn-sm', type: 'button', onclick: async () => {
    toast((await copyText(key)) ? 'Ключ скопирован' : 'Не удалось скопировать, выделите ключ вручную', 'ok');
  } }, 'Скопировать');
  const modal = openModal({
    title,
    content: [
      h('p', {}, h('b', {}, 'Ключ показывается один раз.'), ' Сохраните его: на сервере он не хранится и повторно его не посмотреть.'),
      h('div', { class: 'key-box mono' }, key),
      h('div', { class: 'row' }, copy),
      h('p', { class: 'small muted' }, 'Вставьте ключ при установке клиента на этом ПК: ', h('span', { class: 'mono' }, 'install.ps1 -PcKey …'), '.'),
    ],
    actions: [h('button', { class: 'btn btn-primary', type: 'button', onclick: () => modal.close() }, 'Я сохранил ключ')],
  });
}

export async function rotateKeyFlow(pc) {
  const ok = await confirmDialog({
    title: `Новый ключ для ${pc.name}?`,
    text: 'Старый ключ перестанет работать сразу: клиент на этом ПК потеряет связь, пока вы не пропишете в нём новый ключ.',
    okText: 'Выдать новый ключ', danger: true,
  });
  if (!ok) return;
  try {
    const data = await post(`/api/admin/pcs/${pc.id}/rotate-key`);
    keyDialog(`Новый ключ для ${data.name}`, data.key);
  } catch (e) { toast(e.message, 'error'); }
}

/** Что за ПК в зале-шаблоне, одной строкой для подсказки: «Intel Core i7, RTX 4070, 32 ГБ». */
export function zoneSpecs(z) {
  return [z.cpu, z.gpu, z.ram, z.monitor, z.peripherals, z.note].filter(Boolean).join(', ');
}

/** Добавить ПК. Выбираете зал-шаблон, и цены и характеристики берутся у зала: вводить их заново не нужно. Без залов или без шаблона ПК получает общие тарифы клуба. */
export async function addPcDialog(done) {
  let zones = [];
  let tariffs = [];
  try { [zones, tariffs] = await Promise.all([get('/api/admin/zones'), get('/api/admin/tariffs')]); } catch (e) { toast(e.message, 'error'); return; }
  const name = h('input', { name: 'name', placeholder: 'например, PC-05', maxLength: 50, required: true });
  const zone = h('select', {}, [h('option', { value: '' }, 'Без шаблона (общие тарифы клуба)'),
    ...zones.map((z) => h('option', { value: z.id }, `${z.name}${z.pcs ? `, ${z.pcs} ПК` : ''}`))]);
  if (zones.length) zone.value = String(zones[0].id);
  const preview = h('div', { class: 'small' });
  const error = h('div', { class: 'error-text', role: 'alert' });

  function syncPreview() {
    const z = zones.find((x) => String(x.id) === zone.value);
    const shared = tariffs.filter((t) => t.zone_id == null);
    const own = z ? tariffs.filter((t) => t.zone_id === z.id) : [];
    const list = own.length ? own : shared;
    const lines = [];
    if (z && zoneSpecs(z)) lines.push(h('div', {}, h('span', { class: 'muted' }, 'Характеристики: '), zoneSpecs(z)));
    lines.push(h('div', {}, h('span', { class: 'muted' }, own.length ? 'Тарифы зала: ' : 'Общие тарифы клуба: '),
      list.length ? list.map((t) => `${t.name} ${money(t.price)}`).join(', ') : 'тарифов нет, гости не смогут начать игру'));
    setChildren(preview, lines);
  }
  zone.addEventListener('change', syncPreview);
  syncPreview();

  const save = async () => {
    error.textContent = '';
    if (!name.value.trim()) { error.textContent = 'Введите название ПК'; return; }
    try {
      const data = await post('/api/admin/pcs', { name: name.value.trim(), zone_id: zone.value === '' ? null : Number(zone.value) });
      modal.close();
      keyDialog(`ПК «${data.name}» добавлен`, data.key);
      if (done) done();
    } catch (e) { error.textContent = e.message; }
  };
  const form = h('form', { onsubmit: (ev) => { ev.preventDefault(); save(); } },
    field('Название', name),
    field('Зал', zone, zones.length ? 'Цены и характеристики берутся у зала. Залы настраиваются в разделе «Залы и тарифы»' : 'Залов пока нет: ПК получит общие тарифы. Залы (VIP, Premium, PS5...) заводятся в разделе «Залы и тарифы»'),
    preview, error);
  const modal = openModal({
    title: 'Добавить компьютер', content: form,
    actions: [h('button', { class: 'btn', type: 'button', onclick: () => modal.close() }, 'Отмена'),
      h('button', { class: 'btn btn-primary', type: 'button', onclick: save }, 'Добавить')],
  });
  name.focus();
}

const NOTICE_MAX = 300;
const NOTICE_PHRASES = [
  'Пожалуйста, постарайтесь вести себя потише.',
  'Подойдите, пожалуйста, к администратору.',
  'Напоминаем о правилах клуба.',
  'Ваш заказ готов, подойдите к стойке.',
];

/** Сообщение администратора гостю: текст появляется у него на экране поверх всех окон со звуком. Не нужно идти к ПК:
 * выбрал компьютер на карте, написал (или нажал готовую фразу), отправил. Текст любой, до 300 знаков. */
export function noticeDialog(pc, done) {
  const text = h('textarea', { maxLength: NOTICE_MAX, rows: 4, placeholder: 'Текст сообщения', required: true });
  const counter = h('span', { class: 'small muted' }, `0 / ${NOTICE_MAX}`);
  const error = h('div', { class: 'error-text', role: 'alert' });
  const count = () => { counter.textContent = `${text.value.length} / ${NOTICE_MAX}`; };
  text.addEventListener('input', count);
  const phrases = h('div', { class: 'row', style: 'flex-wrap:wrap;gap:6px;margin-bottom:10px' },
    NOTICE_PHRASES.map((p) => h('button', { class: 'btn btn-sm', type: 'button', onclick: () => { text.value = p; count(); text.focus(); } }, p)));
  const send = async () => {
    error.textContent = '';
    const value = text.value.trim();
    if (!value) { error.textContent = 'Введите текст сообщения'; return; }
    try {
      const r = await post(`/api/admin/pcs/${pc.id}/notice`, { text: value });
      modal.close();
      toast(r.online ? `Сообщение отправлено на ${pc.name}` : `${pc.name} не на связи: сообщение дойдёт, если связь вернётся в течение двух минут`, r.online ? 'ok' : 'error');
      if (done) done();
    } catch (e) { error.textContent = e.message; }
  };
  const modal = openModal({
    title: `Сообщение гостю: ${pc.name}`,
    content: h('form', { onsubmit: (ev) => { ev.preventDefault(); send(); } },
      h('p', { class: 'small muted' }, 'Гость увидит сообщение на экране поверх всех окон, со звуком. Выберите готовую фразу или напишите свою.'),
      phrases, field('Сообщение', text), h('div', { class: 'row' }, h('span', { class: 'grow' }), counter), error),
    actions: [h('button', { class: 'btn', type: 'button', onclick: () => modal.close() }, 'Отмена'),
      h('button', { class: 'btn btn-primary', type: 'button', onclick: send }, 'Отправить')],
  });
  text.focus();
}

/** Гость пришёл к стойке и назвал зал: администратор сразу занимает ПК за ним. Это бронь «на сейчас» на номер гостя: ПК виден занятым,
 * другой гость (в том числе по брони с телефона) на него не сядет и не забронирует. Не сел за 15 минут: ПК освободится сам. */
export function holdPcDialog(pc, done) {
  const phone = phoneInput({ required: true });
  const name = h('input', { maxLength: 80, placeholder: 'необязательно' });
  const length = h('select', {}, HOLD_LENGTHS.map((m) => h('option', { value: m, selected: m === 120 }, minutesText(m))));
  const error = h('div', { class: 'error-text', role: 'alert' });
  const save = async () => {
    error.textContent = '';
    if (!phone.value.trim()) { error.textContent = 'Введите телефон гостя'; return; }
    try {
      await post('/api/admin/bookings', { pc_id: pc.id, phone: phone.value.trim(), name: name.value.trim() || null,
        starts_at: new Date().toISOString(), minutes: Number(length.value) });
      modal.close();
      toast(`${pc.name} занят для гостя`, 'ok');
      if (done) done();
    } catch (e) { error.textContent = e.message; }
  };
  const modal = openModal({
    title: `Занять ${pc.name} для гостя`,
    content: h('form', { onsubmit: (ev) => { ev.preventDefault(); save(); } },
      h('p', { class: 'small muted' }, 'ПК сразу станет занятым: другой гость его не займёт и не забронирует. Если гость не сядет за 15 минут, ПК освободится сам.'),
      field('Телефон гостя', phone), field('Имя', name), field('На сколько', length), error),
    actions: [h('button', { class: 'btn', type: 'button', onclick: () => modal.close() }, 'Отмена'),
      h('button', { class: 'btn btn-primary', type: 'button', onclick: save }, 'Занять ПК')],
  });
  phone.focus();
}

/** Запуск сессии сотрудником: сначала гость (по последним цифрам телефона), потом тариф, который он может оплатить. */
export async function startSessionDialog(pc, done) {
  let guest = null;
  let tariff = null;
  let tariffs = [];
  const search = digitsInput({ placeholder: 'Последние цифры телефона (от 3)' });
  const guestList = h('div', { class: 'pick-list' });
  const tariffList = h('div', { class: 'pick-list' });
  const error = h('div', { class: 'error-text', role: 'alert' });
  const start = h('button', { class: 'btn btn-primary', type: 'button', disabled: true, onclick: doStart }, 'Запустить сессию');

  function pickTariff(t) { tariff = t; renderTariffs(); }
  function renderTariffs() {
    start.disabled = !(guest && tariff);
    setChildren(tariffList, tariffs.map((t) => {
      const missing = guest ? minBalance(t) - guest.balance : 0;
      const blocked = !guest || missing > 0;
      return h('button', { class: `pick${tariff && tariff.id === t.id ? ' selected' : ''}`, type: 'button', disabled: blocked, onclick: () => pickTariff(t) },
        h('span', { class: 'grow' }, h('b', {}, t.name), ' ', badge(kindName(t.kind)), h('div', { class: 'small muted' }, tariffDescribe(t))),
        guest && missing > 0 ? h('span', { class: 'small warn' }, `не хватает ${money(missing)}`) : null);
    }));
  }

  function chooseGuest(acc) {
    guest = acc;
    tariff = tariff && guest.balance >= minBalance(tariff) ? tariff : null;
    renderGuests([acc]);
    renderTariffs();
  }

  function renderGuests(list) {
    if (list.length === 0) { setChildren(guestList, h('div', { class: 'small muted' }, 'Гость не найден. Он должен сначала зарегистрироваться на ПК.')); return; }
    setChildren(guestList, list.map((acc) => h('button', {
      class: `pick${guest && guest.id === acc.id ? ' selected' : ''}`, type: 'button', disabled: acc.status !== 'active', onclick: () => chooseGuest(acc),
    }, h('span', { class: 'grow' }, h('b', {}, phonePretty(acc.phone))), acc.status !== 'active' ? badge('заблокирован', 'danger') : null, h('span', { class: 'muted' }, money(acc.balance)))));
  }

  let timer;
  let searchId = 0;   // защита от «старый медленный ответ перекрыл новый»
  search.addEventListener('input', () => {
    clearTimeout(timer);
    guest = null;               // поиск изменили: прежний выбор гостя больше недействителен
    renderTariffs();
    timer = setTimeout(async () => {
      const mine = ++searchId;
      const digits = search.value.replace(/\D/g, '');
      if (digits.length < 3) { setChildren(guestList); return; }
      try {
        const list = await get(`/api/admin/accounts?q=${encodeURIComponent(digits)}&limit=6`);
        if (mine !== searchId) return;
        error.textContent = '';
        renderGuests(list);
        if (list.length === 1 && list[0].status === 'active') chooseGuest(list[0]);   // один подходящий: выбираем сами
      } catch (e) { error.textContent = e.message; }
    }, 250);
  });

  async function doStart() {
    error.textContent = '';
    start.disabled = true;
    try {
      await post('/api/admin/sessions', { pc_id: pc.id, tariff_id: tariff.id, account_id: guest.id });
      modal.close();
      toast(`Сессия запущена на ${pc.name}`, 'ok');
      if (done) done();
    } catch (e) { error.textContent = e.message; start.disabled = false; }
  }

  const modal = openModal({
    title: `Запустить сессию: ${pc.name}`, wide: true,
    content: [field('1. Гость', search), guestList, h('div', { class: 'field' }, h('span', { class: 'small muted' }, '2. Тариф (оплачивается с баланса гостя)')), tariffList, error],
    actions: [h('button', { class: 'btn', type: 'button', onclick: () => modal.close() }, 'Отмена'), start],
    onClose: () => clearTimeout(timer),
  });
  search.focus();
  try { tariffs = await get(`/api/admin/tariffs?pc_id=${pc.id}`); renderTariffs(); } catch (e) { error.textContent = e.message; }   // только тарифы зала этого ПК
}
