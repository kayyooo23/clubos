// Обращения к серверу. Токен входа хранится в sessionStorage: он пропадает при закрытии вкладки.

const TOKEN_KEY = 'clubos.admin.token';

export class ApiError extends Error {
  constructor(message, status) { super(message); this.status = status; }
  get isNetwork() { return this.status === 0; }
}

const listeners = new Set();
/** Подписка на «вход перестал действовать» (сервер ответил 401). */
export function onUnauthorized(fn) { listeners.add(fn); return () => listeners.delete(fn); }

function safeStorage() { try { return window.sessionStorage; } catch { return null; } }
export function getToken() { return safeStorage()?.getItem(TOKEN_KEY) || null; }
export function setToken(token) {
  const store = safeStorage();
  if (!store) return;
  if (token) store.setItem(TOKEN_KEY, token); else store.removeItem(TOKEN_KEY);
}

/** Сервер шлёт {"detail": "текст"} или {"detail": [{"msg": ...}]} — приводим к понятной строке. */
export function errorText(payload, status) {
  const detail = payload && payload.detail;
  if (typeof detail === 'string') return detail;
  if (Array.isArray(detail)) {
    const messages = detail.map((d) => (d && d.msg ? String(d.msg).replace(/^Value error, /, '') : '')).filter(Boolean);
    if (messages.length) return messages.join('; ');
    return 'Проверьте введённые данные';
  }
  return `Ошибка сервера (${status})`;
}

export async function api(method, path, body, { auth = true } = {}) {
  const headers = {};
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const token = getToken();
  if (auth && token) headers.Authorization = `Bearer ${token}`;
  let response;
  try {
    response = await fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  } catch {
    throw new ApiError('Нет связи с сервером', 0);
  }
  let payload = null;
  try { payload = await response.json(); } catch { /* пустой или не-JSON ответ */ }
  if (!response.ok) {
    if (response.status === 401 && auth && token) { setToken(null); listeners.forEach((fn) => fn()); }
    throw new ApiError(errorText(payload, response.status), response.status);
  }
  return payload;
}

/** Загрузка файла как есть (картинка): тело запроса это сам файл. Возвращает JSON ответа сервера. */
export async function uploadFile(path, file) {
  const headers = { 'Content-Type': file.type || 'application/octet-stream' };
  const token = getToken();
  if (token) headers.Authorization = `Bearer ${token}`;
  let response;
  try {
    response = await fetch(path, { method: 'PUT', headers, body: file });
  } catch {
    throw new ApiError('Нет связи с сервером', 0);
  }
  let payload = null;
  try { payload = await response.json(); } catch { /* не JSON */ }
  if (!response.ok) {
    if (response.status === 401 && token) { setToken(null); listeners.forEach((fn) => fn()); }
    throw new ApiError(errorText(payload, response.status), response.status);
  }
  return payload;
}

/** Картинка с сервера как data:-адрес: страница показывает картинки только со своего адреса и из data:, а запросу нужен токен входа. */
export async function imageDataUrl(path) {
  const headers = {};
  const token = getToken();
  if (token) headers.Authorization = `Bearer ${token}`;
  let response;
  try {
    response = await fetch(path, { headers });
  } catch {
    throw new ApiError('Нет связи с сервером', 0);
  }
  if (!response.ok) throw new ApiError('Картинка не загрузилась', response.status);
  const blob = await response.blob();
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result));
    reader.onerror = () => reject(new ApiError('Не удалось прочитать картинку', 0));
    reader.readAsDataURL(blob);
  });
}

export const get = (path) => api('GET', path);
export const post = (path, body) => api('POST', path, body === undefined ? {} : body);
export const patch = (path, body) => api('PATCH', path, body);
export const del = (path) => api('DELETE', path);

export async function login(club, phone, password) {
  const data = await api('POST', '/api/admin/auth/login', { club, phone, password }, { auth: false });
  setToken(data.token);
}

export async function logout() {
  try { await post('/api/admin/auth/logout'); } catch { /* вход и так мог истечь */ }
  setToken(null);
}
