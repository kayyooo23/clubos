// Форматирование денег, времени и названий. Время сервер хранит и отдаёт в UTC без пометки пояса;
// показываем его по часовому поясу КЛУБА (utc_offset), а не браузера: кассир может сидеть за чужим компьютером.

const HOUR = 3600 * 1000;

export const money = (n) => `${new Intl.NumberFormat('ru-RU').format(Math.round(Number(n) || 0))} ₽`;

/** '2026-10-06T09:11:09.727397' (UTC) → Date */
export function parseUtc(value) {
  if (!value) return null;
  let s = String(value).replace(/(\.\d{3})\d+/, '$1');
  if (!/(Z|[+-]\d\d:?\d\d)$/.test(s)) s += 'Z';
  const d = new Date(s);
  return Number.isNaN(d.getTime()) ? null : d;
}

const two = (n) => String(n).padStart(2, '0');

/** Сдвигает момент в «настенное» время клуба; дальше читаем getUTC*. */
const wall = (date, offset) => new Date(date.getTime() + offset * HOUR);

export function fmtDateTime(value, offset) {
  const d = parseUtc(value);
  if (!d) return '-';
  const w = wall(d, offset);
  return `${two(w.getUTCDate())}.${two(w.getUTCMonth() + 1)} ${two(w.getUTCHours())}:${two(w.getUTCMinutes())}`;
}

export function fmtTime(value, offset) {
  const d = parseUtc(value);
  if (!d) return '-';
  const w = wall(d, offset);
  return `${two(w.getUTCHours())}:${two(w.getUTCMinutes())}:${two(w.getUTCSeconds())}`;
}

/** Значение для <input type="datetime-local"> («YYYY-MM-DDTHH:mm») в часовом поясе клуба. */
export function toLocalInput(date, offset) {
  const w = wall(date, offset);
  return `${w.getUTCFullYear()}-${two(w.getUTCMonth() + 1)}-${two(w.getUTCDate())}T${two(w.getUTCHours())}:${two(w.getUTCMinutes())}`;
}

/** Обратно: то, что ввели в datetime-local (время клуба) → строка UTC для API. */
export function localInputToUtcIso(value, offset) {
  const m = /^(\d{4})-(\d\d)-(\d\d)T(\d\d):(\d\d)/.exec(value || '');
  if (!m) return null;
  const ms = Date.UTC(+m[1], +m[2] - 1, +m[3], +m[4], +m[5]) - offset * HOUR;
  return new Date(ms).toISOString();
}

/** Полночь по времени клуба: daysAgo=0 — сегодня, 1 — вчера. Возвращает Date (момент в UTC). */
export function startOfClubDay(offset, daysAgo = 0, now = new Date()) {
  const w = wall(now, offset);
  const midnight = Date.UTC(w.getUTCFullYear(), w.getUTCMonth(), w.getUTCDate() - daysAgo);
  return new Date(midnight - offset * HOUR);
}

/** 3725 → «1:02:05», 125 → «02:05». */
export function clock(seconds) {
  const s = Math.max(0, Math.floor(seconds));
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
  return h > 0 ? `${h}:${two(m)}:${two(sec)}` : `${two(m)}:${two(sec)}`;
}

/** +79990000001 → +7 999 000-00-01 (российский формат; всё остальное — как есть). */
/** Десять цифр российского номера без кода страны из любого текста: ввод руками, вставка «8 900 123-45-67», уже отформатированная строка. */
export function phoneDigits(text) {
  const raw = String(text ?? '');
  let digits = raw.replace(/\D/g, '');
  if (/^\s*\+7/.test(raw)) digits = digits.slice(1);              // «7» из «+7» не часть номера
  else if (digits.length >= 11 && (digits[0] === '7' || digits[0] === '8')) digits = digits.slice(1);
  return digits.slice(0, 10);
}

/** Цифры номера в маску «+7 (900) 123-45-67»; пусто, пока цифр нет (тогда в поле видна подсказка). */
export function phoneMask(digits) {
  if (!digits) return '';
  let out = `+7 (${digits.slice(0, 3)}`;
  if (digits.length > 3) out += `) ${digits.slice(3, 6)}`;
  if (digits.length > 6) out += `-${digits.slice(6, 8)}`;
  if (digits.length > 8) out += `-${digits.slice(8, 10)}`;
  return out;
}

export function phonePretty(phone) {
  const m = /^\+7(\d{3})(\d{3})(\d\d)(\d\d)$/.exec(phone || '');
  return m ? `+7 ${m[1]} ${m[2]}-${m[3]}-${m[4]}` : (phone || '');
}

export function durationText(fromValue, toValue) {
  const a = parseUtc(fromValue), b = parseUtc(toValue);
  if (!a || !b) return '-';
  const minutes = Math.max(0, Math.round((b - a) / 60000));
  const h = Math.floor(minutes / 60), m = minutes % 60;
  return h ? `${h} ч ${m} мин` : `${m} мин`;
}

// ---------- тарифы ----------
export const KINDS = { hourly: 'Почасовой', package: 'Пакет', night: 'Ночной' };
export const kindName = (kind) => KINDS[kind] || kind;

export function minutesText(minutes) {
  const h = Math.floor(minutes / 60), m = minutes % 60;
  if (h === 0) return `${m} мин`;
  return m === 0 ? `${h} ч` : `${h} ч ${m} мин`;
}

export function tariffDescribe(t) {
  if (t.kind === 'hourly') return `${money(t.price)} в час, списывается по факту`;
  if (t.kind === 'package') return `${money(t.price)} за ${minutesText(t.package_minutes || 0)}`;
  if (t.kind === 'night') return `${money(t.price)}, с ${two(t.night_start)}:00 до ${two(t.night_end)}:00`;
  return money(t.price);
}

/** Сколько должно быть на балансе для старта (как на сервере: для почасового — на 10 минут). */
export function minBalance(t) {
  return t.kind === 'hourly' ? Math.ceil((t.price * 10) / 60) : t.price;
}

// ---------- подписи ----------
const ACTIONS = {
  admin_login: 'Вход в админку',
  staff_unlock: 'Сотрудник вошёл на ПК (рабочий стол)',
  staff_lock: 'Сотрудник вышел с ПК',
  pc_force_lock: 'Принудительная блокировка ПК',
  pc_key_rotated: 'Смена ключа ПК',
  topup_cash: 'Пополнение наличными',
  adjustment: 'Корректировка баланса',
  account_block: 'Блокировка гостя',
  account_unblock: 'Разблокировка гостя',
  session_start_by_staff: 'Сессия запущена сотрудником',
  session_stop_by_staff: 'Сессия остановлена сотрудником',
  password_reset: 'Гость сбросил пароль',
  staff_password_reset_by_platform: 'Пароль сотрудника сброшен платформой',
  app_create: 'Игра добавлена в меню',
  app_update: 'Запись меню изменена',
  app_delete: 'Игра удалена из меню',
  apps_copied: 'Каталог игр скопирован из другого клуба',
  app_cover_set: 'Обложка игры загружена',
  app_cover_delete: 'Обложка игры удалена',
  covers_steam: 'Обложки подтянуты из Steam',
  booking_create: 'ПК забронирован',
  ads_enabled: 'Реклама партнёров включена',
  ads_disabled: 'Реклама партнёров выключена',
  booking_update: 'Бронь изменена',
  booking_cancel: 'Бронь отменена',
  pc_update: 'ПК изменён (название, заметка, место на карте)',
  pc_layout: 'Расстановка ПК на карте сохранена',
  pc_maintenance_on: 'ПК поставлен на обслуживание',
  pc_maintenance_off: 'ПК снят с обслуживания',
  product_create: 'Товар добавлен в бар',
  product_update: 'Товар в баре изменён',
  product_delete: 'Товар удалён из бара',
  order_accepted: 'Заказ принят в работу',
  order_done: 'Заказ выдан',
  order_cancelled: 'Заказ отменён, деньги возвращены',
};
export const actionLabel = (action) => ACTIONS[action] || action;
export const ACTION_KEYS = Object.keys(ACTIONS);

const TX_KINDS = {
  topup_cash: 'Пополнение (наличные)',
  shop_purchase: 'Заказ в баре',
  shop_refund: 'Возврат за заказ',
  topup_online: 'Пополнение онлайн',
  session_charge: 'Оплата сессии',
  adjustment: 'Корректировка',
};
export const txKindName = (kind) => TX_KINDS[kind] || kind;

/** 'account=5 amount=300' → «гость №5, 300 ₽». Остальное показываем как есть. */
export function auditDetails(details) {
  if (!details) return '';
  const parts = {};
  for (const piece of details.split(/\s+/)) {
    const i = piece.indexOf('=');
    if (i > 0) parts[piece.slice(0, i)] = piece.slice(i + 1);
  }
  const out = [];
  if (parts.account) out.push(`гость №${parts.account}`);
  if (parts.session) out.push(`сессия №${parts.session}`);
  if (parts.app) out.push(`запись №${parts.app}`);
  if (parts.product) out.push(`товар №${parts.product}`);
  if (parts.booking) out.push(`бронь №${parts.booking}`);
  if (parts.order) out.push(`заказ №${parts.order}`);
  if (parts.count && parts.from_club) out.push(`записей: ${parts.count}`);
  if (parts.amount && !Number.isNaN(Number(parts.amount))) {
    const n = Number(parts.amount);
    out.push(`${n > 0 ? '+' : ''}${money(n)}`);
  }
  return out.length ? out.join(', ') : details;
}
