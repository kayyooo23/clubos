// Окна для работы с одним ПК: добавить ПК, выдать ключ, запустить сессию за гостя. Раньше жили на вкладке «Компьютеры», теперь всё делается на карте клуба.
import { h, setChildren, badge, openModal, confirmDialog, toast, field, copyText, digitsInput } from '../dom.js';
import { get, post } from '../api.js';
import { money, phonePretty, tariffDescribe, minBalance, kindName } from '../format.js';

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

export function addPcDialog(done) {
  const name = h('input', { name: 'name', placeholder: 'например, PC-05', maxLength: 50, required: true });
  const error = h('div', { class: 'error-text', role: 'alert' });
  const save = async () => {
    error.textContent = '';
    if (!name.value.trim()) { error.textContent = 'Введите название ПК'; return; }
    try {
      const data = await post('/api/admin/pcs', { name: name.value.trim() });
      modal.close();
      keyDialog(`ПК «${data.name}» добавлен`, data.key);
      if (done) done();
    } catch (e) { error.textContent = e.message; }
  };
  const form = h('form', { onsubmit: (ev) => { ev.preventDefault(); save(); } }, field('Название', name), error);
  const modal = openModal({
    title: 'Добавить компьютер', content: form,
    actions: [h('button', { class: 'btn', type: 'button', onclick: () => modal.close() }, 'Отмена'),
      h('button', { class: 'btn btn-primary', type: 'button', onclick: save }, 'Добавить')],
  });
  name.focus();
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
  try { tariffs = await get('/api/admin/tariffs'); renderTariffs(); } catch (e) { error.textContent = e.message; }
}
