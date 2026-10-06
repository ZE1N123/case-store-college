const $ = (selector, root = document) => root.querySelector(selector);
const state = { cases: [], posts: [], settings: {}, account: null, cart: new Map(), authMode: "login", adminTab: "cases" };
const money = (value) => `${Number(value).toLocaleString("ru-RU")} ₸`;
const escapeHtml = (value = "") => String(value).replace(/[&<>"']/g, (char) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[char]);
let toastTimer;

async function api(url, options = {}) {
  const headers = new Headers(options.headers || {});
  if (options.body && !(options.body instanceof FormData)) headers.set("Content-Type", "application/json");
  const response = await fetch(url, { ...options, headers, credentials: "same-origin" });
  if (!response.ok) {
    const problem = await response.json().catch(() => ({}));
    throw new Error(problem.message || `Ошибка запроса (${response.status})`);
  }
  return response.status === 204 ? null : response.json();
}

function toast(message) {
  const element = $("#toast");
  element.textContent = message;
  element.classList.add("show");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => element.classList.remove("show"), 2600);
}

async function loadStore() {
  const data = await api("/api/store");
  state.cases = data.cases || [];
  state.posts = data.posts || [];
  state.settings = data.settings || {};
  renderCases();
  renderBlog();
  renderSettings();
}

async function loadAccount() {
  try {
    state.account = await api("/api/account");
  } catch (error) {
    if (error.message.startsWith("Ошибка запроса (401)")) state.account = null;
    else throw error;
  }
  $("#account-label").textContent = state.account ? state.account.username : "Аккаунт";
}

function renderCases() {
  const list = $("#case-list");
  $("#catalog-count").textContent = String(state.cases.length).padStart(2, "0");
  if (!state.cases.length) {
    list.innerHTML = '<p class="empty-note">Кейсы скоро появятся.</p>';
    return;
  }
  list.innerHTML = state.cases.map((item, index) => `
    <article class="case-card">
      <div class="case-image">${item.imageUrl ? `<img src="${escapeHtml(item.imageUrl)}" alt="${escapeHtml(item.name)}" loading="lazy">` : ""}
        ${item.tag ? `<span class="case-tag">${escapeHtml(item.tag)}</span>` : ""}<span class="case-number">CASE Nº ${String(index + 1).padStart(3, "0")}</span>
      </div>
      <div class="case-info"><h3>${escapeHtml(item.name)}</h3><p>${escapeHtml(item.description)}</p>
        <div class="case-bottom"><span class="case-price">${money(item.price)} <small>/ кейс</small></span>
        <button class="add-button" data-add="${item.id}">В корзину +</button></div>
      </div>
    </article>`).join("");
}

function renderBlog() {
  const list = $("#blog-list");
  if (!state.posts.length) {
    list.innerHTML = '<p class="empty-note">Скоро здесь появятся новости.</p>';
    return;
  }
  list.innerHTML = state.posts.map((post) => `
    <article class="blog-card">
      ${post.mediaUrl ? `<div class="blog-media">${post.mediaType === "video" ? `<video src="${escapeHtml(post.mediaUrl)}" controls preload="metadata"></video>` : `<img src="${escapeHtml(post.mediaUrl)}" alt="${escapeHtml(post.title)}" loading="lazy">`}</div>` : ""}
      <div class="blog-body"><span class="blog-date">${new Date(post.createdAt).toLocaleDateString("ru-RU")}</span><h3>${escapeHtml(post.title)}</h3><p>${escapeHtml(post.content)}</p></div>
    </article>`).join("");
}

function renderSettings() {
  if (state.settings.storeName) {
    document.title = `${state.settings.storeName} — магазин кейсов`;
    const [first, ...rest] = state.settings.storeName.split(/\s+/);
    $(".brand-name").innerHTML = `${escapeHtml(first)}${rest.length ? `<span>${escapeHtml(rest.join(" "))}</span>` : ""}`;
  }
  const link = $("#whatsapp-link");
  link.href = state.settings.whatsAppUrl || "#";
  link.textContent = `${state.settings.whatsAppLabel || "Написать нам"} ↗`;
  link.style.display = state.settings.whatsAppUrl ? "" : "none";
}

function renderCartCount() {
  $("#cart-count").textContent = [...state.cart.values()].reduce((sum, count) => sum + count, 0);
}

function showModal(content) {
  $("#modal-content").innerHTML = content;
  $("#modal-backdrop").hidden = false;
}

function closeModal() {
  $("#modal-backdrop").hidden = true;
}

function openAuth(mode = "login", error = "") {
  state.authMode = mode;
  const register = mode === "register";
  showModal(`<h2 id="modal-title">${register ? "СОЗДАТЬ АККАУНТ" : "С ВОЗВРАЩЕНИЕМ"}</h2>
    <p class="modal-intro">${register ? "Зарегистрируйся, чтобы сохранять историю заказов." : "Войди в аккаунт, чтобы продолжить."}</p>
    <div class="modal-tabs"><button class="${!register ? "active" : ""}" data-auth-mode="login">Войти</button><button class="${register ? "active" : ""}" data-auth-mode="register">Регистрация</button></div>
    <form id="auth-form"><div class="field"><label for="auth-username">ИМЯ ПОЛЬЗОВАТЕЛЯ</label><input id="auth-username" name="username" minlength="3" maxlength="30" autocomplete="username" required></div>
    ${register ? '<div class="field"><label for="auth-email">EMAIL (НЕОБЯЗАТЕЛЬНО)</label><input id="auth-email" name="email" type="email" maxlength="120" autocomplete="email"></div>' : ""}
    <div class="field"><label for="auth-password">ПАРОЛЬ ${register ? "(МИНИМУМ 8 СИМВОЛОВ)" : ""}</label><input id="auth-password" name="password" type="password" minlength="${register ? 8 : 1}" maxlength="128" autocomplete="${register ? "new-password" : "current-password"}" required></div>
    <p class="form-error">${escapeHtml(error)}</p><button class="button button-primary modal-submit" type="submit">${register ? "Создать аккаунт" : "Войти"} <span>↗</span></button></form>`);
}

async function openAccount() {
  if (!state.account) {
    openAuth();
    return;
  }
  const orders = state.account.orders || [];
  showModal(`<h2 id="modal-title">МОЙ АККАУНТ</h2><p class="modal-intro">Личный кабинет CASE ROOM</p>
    <div class="account-summary"><strong>${escapeHtml(state.account.username)}</strong>${state.account.email ? ` · ${escapeHtml(state.account.email)}` : ""}${state.account.isAdmin ? " · Администратор" : ""}</div>
    ${state.account.isAdmin ? '<button class="button button-primary" id="open-admin">Панель управления ↗</button>' : ""}
    <h3>История заказов</h3>${orders.length ? orders.map((order) => `<div class="order-row"><strong>${money(order.total)} · ${new Date(order.createdAt).toLocaleDateString("ru-RU")}</strong>${order.items.map((item) => `${escapeHtml(item.name)} × ${item.quantity}`).join(", ")}</div>`).join("") : '<p class="empty-note">Заказов пока нет.</p>'}
    <button class="small-button" id="logout-button">Выйти из аккаунта</button>`);
}

function openCart() {
  const entries = [...state.cart.entries()].map(([id, quantity]) => ({ item: state.cases.find((item) => item.id === id), quantity })).filter((entry) => entry.item);
  const total = entries.reduce((sum, entry) => sum + Number(entry.item.price) * entry.quantity, 0);
  showModal(`<h2 id="modal-title">ТВОЯ КОРЗИНА</h2><p class="modal-intro">Проверь заказ перед оформлением.</p>
    ${entries.length ? entries.map(({ item, quantity }) => `<div class="admin-row"><span>${escapeHtml(item.name)} × ${quantity}</span><strong>${money(item.price * quantity)}</strong></div>`).join("") : '<p class="empty-note">Пока пусто — самое время выбрать кейс.</p>'}
    <div class="account-summary">Итого: <strong>${money(total)}</strong></div>
    ${entries.length ? '<button class="button button-primary modal-submit" id="checkout-button">Оформить заказ <span>↗</span></button><p class="modal-note">Учебный проект: оплата не подключена.</p>' : '<button class="button button-primary modal-submit" id="back-to-cases">Перейти к кейсам <span>↗</span></button>'}`);
}

function field(label, name, value = "", type = "text", extra = "") {
  return `<div class="field"><label for="field-${name}">${label}</label><input id="field-${name}" name="${name}" type="${type}" value="${escapeHtml(value)}" ${extra}></div>`;
}

function adminEditor() {
  const tab = state.adminTab;
  let content = "";
  if (tab === "cases") {
    content = `<div class="admin-list">${state.cases.map((item) => `<div class="admin-row"><span>${escapeHtml(item.name)} · ${money(item.price)}</span><span class="admin-row-actions"><button class="small-button" data-edit-case="${item.id}">Изменить</button><button class="small-button danger" data-delete-case="${item.id}">Удалить</button></span></div>`).join("") || '<p class="empty-note">Кейсов пока нет.</p>'}</div>
      <div class="admin-panel"><h3 id="case-form-title">Добавить кейс</h3><form id="case-form">${field("НАЗВАНИЕ", "name", "", "text", 'maxlength="60" required')}${field("ОПИСАНИЕ", "description", "", "text", 'maxlength="400" required')}${field("ЦЕНА (₸)", "price", "", "number", 'min="0" max="1000000" required')}${field("ССЫЛКА НА КАРТИНКУ", "imageUrl")}<div class="field"><label for="case-file">ИЛИ ЗАГРУЗИ ФОТО</label><input id="case-file" name="file" type="file" accept="image/jpeg,image/png,image/webp,image/gif"><span class="file-hint">JPG, PNG, WebP, GIF · до 20 МБ</span></div>${field("МЕТКА", "tag")}${field("АКЦЕНТНЫЙ ЦВЕТ", "accent", "#e9a640", "color")}<input name="id" type="hidden"><button class="button button-primary modal-submit">Сохранить кейс ↗</button></form></div>`;
  } else if (tab === "posts") {
    content = `<div class="admin-list">${state.posts.map((post) => `<div class="admin-row"><span>${escapeHtml(post.title)}</span><span class="admin-row-actions"><button class="small-button" data-edit-post="${post.id}">Изменить</button><button class="small-button danger" data-delete-post="${post.id}">Удалить</button></span></div>`).join("") || '<p class="empty-note">Публикаций пока нет.</p>'}</div>
      <div class="admin-panel"><h3 id="post-form-title">Новая публикация</h3><form id="post-form">${field("ЗАГОЛОВОК", "title", "", "text", 'maxlength="100" required')}<div class="field"><label for="field-content">ТЕКСТ</label><textarea id="field-content" name="content" maxlength="5000" required></textarea></div>
      <div class="field"><label for="post-file">ФОТО ИЛИ ВИДЕО (ДО 20 МБ)</label><input id="post-file" name="file" type="file" accept="image/jpeg,image/png,image/webp,image/gif,video/mp4,video/webm"><span class="file-hint">JPG, PNG, WebP, GIF, MP4, WebM</span></div>
      ${field("ИЛИ ССЫЛКА НА МЕДИА", "mediaUrl")}<div class="field"><label for="field-mediaType">ТИП МЕДИА</label><select id="field-mediaType" name="mediaType"><option value="image">Фотография</option><option value="video">Видео</option></select></div><input name="id" type="hidden"><button class="button button-primary modal-submit">Опубликовать ↗</button></form></div>`;
  } else {
    content = `<div class="admin-panel"><form id="settings-form">${field("НАЗВАНИЕ МАГАЗИНА", "storeName", state.settings.storeName || "CASE ROOM", "text", 'maxlength="50" required')}${field("ССЫЛКА WHATSAPP (HTTPS)", "whatsAppUrl", state.settings.whatsAppUrl || "", "url")}${field("ТЕКСТ КНОПКИ", "whatsAppLabel", state.settings.whatsAppLabel || "Написать нам", "text", 'maxlength="40"')}<button class="button button-primary modal-submit">Сохранить настройки ↗</button></form></div>`;
  }
  showModal(`<h2 id="modal-title">ПАНЕЛЬ УПРАВЛЕНИЯ</h2><p class="modal-intro">Управление витриной и публикациями.</p>
    <div class="admin-tabs"><button class="${tab === "cases" ? "active" : ""}" data-admin-tab="cases">Кейсы и цены</button><button class="${tab === "posts" ? "active" : ""}" data-admin-tab="posts">Блог</button><button class="${tab === "settings" ? "active" : ""}" data-admin-tab="settings">Настройки</button></div>${content}`);
}

function editCase(id) {
  const item = state.cases.find((candidate) => candidate.id === id);
  const form = $("#case-form");
  if (!item || !form) return;
  for (const [key, value] of Object.entries(item)) {
    if (form.elements.namedItem(key)) form.elements.namedItem(key).value = value;
  }
  form.elements.namedItem("id").value = item.id;
  $("#case-form-title").textContent = "Изменить кейс";
  form.scrollIntoView({ behavior: "smooth", block: "start" });
}

function editPost(id) {
  const post = state.posts.find((candidate) => candidate.id === id);
  const form = $("#post-form");
  if (!post || !form) return;
  for (const [key, value] of Object.entries(post)) {
    if (form.elements.namedItem(key)) form.elements.namedItem(key).value = value;
  }
  form.elements.namedItem("id").value = post.id;
  $("#post-form-title").textContent = "Изменить публикацию";
  form.scrollIntoView({ behavior: "smooth", block: "start" });
}

document.addEventListener("click", async (event) => {
  const target = event.target.closest("button, a");
  if (!target) return;
  try {
    if (target.id === "theme-toggle") {
      document.body.classList.toggle("day");
      localStorage.setItem("case-store-theme", document.body.classList.contains("day") ? "day" : "night");
    } else if (target.id === "menu-toggle") {
      $(".nav").classList.toggle("open");
    } else if (target.matches(".nav a")) {
      $(".nav").classList.remove("open");
    } else if (target.id === "account-button") {
      await loadAccount();
      await openAccount();
    } else if (target.id === "cart-button") {
      openCart();
    } else if (target.id === "modal-close" || target.id === "modal-backdrop") {
      closeModal();
    } else if (target.dataset.authMode) {
      openAuth(target.dataset.authMode);
    } else if (target.dataset.add) {
      state.cart.set(target.dataset.add, (state.cart.get(target.dataset.add) || 0) + 1);
      renderCartCount();
      toast("Кейс добавлен в корзину");
    } else if (target.id === "checkout-button") {
      if (!state.account) {
        openAuth();
        return;
      }
      const order = await api("/api/orders", { method: "POST", body: JSON.stringify({ items: [...state.cart].map(([caseId, quantity]) => ({ caseId, quantity })) }) });
      state.cart.clear();
      renderCartCount();
      await loadAccount();
      showModal(`<h2 id="modal-title">ЗАКАЗ ОФОРМЛЕН</h2><p class="modal-intro">Заказ добавлен в историю аккаунта. Это учебный проект — оплата не списывается.</p><div class="account-summary">Сумма заказа: <strong>${money(order.total)}</strong></div><button class="button button-primary modal-submit" id="modal-close">Отлично</button>`);
    } else if (target.id === "back-to-cases") {
      closeModal();
      location.hash = "#cases";
    } else if (target.id === "logout-button") {
      await api("/api/auth/logout", { method: "POST" });
      state.account = null;
      $("#account-label").textContent = "Аккаунт";
      closeModal();
      toast("Вы вышли из аккаунта");
    } else if (target.id === "open-admin") {
      state.adminTab = "cases";
      adminEditor();
    } else if (target.dataset.adminTab) {
      state.adminTab = target.dataset.adminTab;
      adminEditor();
    } else if (target.dataset.editCase) {
      editCase(target.dataset.editCase);
    } else if (target.dataset.editPost) {
      editPost(target.dataset.editPost);
    } else if (target.dataset.deleteCase) {
      if (confirm("Удалить этот кейс?")) {
        await api(`/api/admin/cases/${target.dataset.deleteCase}`, { method: "DELETE" });
        await loadStore();
        adminEditor();
        toast("Кейс удалён");
      }
    } else if (target.dataset.deletePost) {
      if (confirm("Удалить эту публикацию?")) {
        await api(`/api/admin/posts/${target.dataset.deletePost}`, { method: "DELETE" });
        await loadStore();
        adminEditor();
        toast("Публикация удалена");
      }
    }
  } catch (error) {
    toast(error.message);
  }
});

$("#modal-backdrop").addEventListener("click", (event) => {
  if (event.target.id === "modal-backdrop") closeModal();
});

document.addEventListener("submit", async (event) => {
  const form = event.target;
  event.preventDefault();
  try {
    if (form.id === "auth-form") {
      const data = Object.fromEntries(new FormData(form));
      const mode = state.authMode;
      await api(mode === "register" ? "/api/auth/register" : "/api/auth/login", { method: "POST", body: JSON.stringify(data) });
      await loadAccount();
      toast(mode === "register" ? "Аккаунт создан" : "Вы вошли в аккаунт");
      await openAccount();
    } else if (form.id === "case-form") {
      const data = Object.fromEntries(new FormData(form));
      const id = data.id;
      delete data.id;
      const file = form.elements.namedItem("file").files[0];
      delete data.file;
      if (file) {
        const upload = new FormData();
        upload.append("file", file);
        const result = await api("/api/admin/upload", { method: "POST", body: upload });
        data.imageUrl = result.url;
      }
      data.price = Number(data.price);
      await api(id ? `/api/admin/cases/${id}` : "/api/admin/cases", { method: id ? "PUT" : "POST", body: JSON.stringify(data) });
      await loadStore();
      adminEditor();
      toast(id ? "Кейс обновлён" : "Кейс добавлен");
    } else if (form.id === "post-form") {
      const data = Object.fromEntries(new FormData(form));
      const id = data.id;
      delete data.id;
      const file = form.elements.namedItem("file").files[0];
      if (file) {
        const upload = new FormData();
        upload.append("file", file);
        const result = await api("/api/admin/upload", { method: "POST", body: upload });
        data.mediaUrl = result.url;
        data.mediaType = file.type.startsWith("video/") ? "video" : "image";
      }
      await api(id ? `/api/admin/posts/${id}` : "/api/admin/posts", { method: id ? "PUT" : "POST", body: JSON.stringify(data) });
      await loadStore();
      adminEditor();
      toast(id ? "Публикация обновлена" : "Публикация добавлена");
    } else if (form.id === "settings-form") {
      const data = Object.fromEntries(new FormData(form));
      await api("/api/admin/settings", { method: "PUT", body: JSON.stringify(data) });
      await loadStore();
      adminEditor();
      toast("Настройки сохранены");
    }
  } catch (error) {
    const errorElement = $(".form-error", form);
    if (errorElement) errorElement.textContent = error.message;
    else toast(error.message);
  }
});

if (localStorage.getItem("case-store-theme") === "day") document.body.classList.add("day");
loadStore().catch((error) => {
  $("#case-list").innerHTML = `<p class="empty-note">${escapeHtml(error.message)}. Проверьте, что запущен ASP.NET Core сервер.</p>`;
});
loadAccount().catch((error) => toast(error.message));
