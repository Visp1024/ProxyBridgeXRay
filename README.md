# ProxyBridge + XRay Reality — Windows GUI

<p align="center">
  <img src="img/logo.png" alt="ProxyBridge Logo" />
</p>

**ProxyBridge** is a lightweight, open-source Windows proxy client (Proxifier alternative) that transparently routes TCP and UDP traffic from specific applications through SOCKS5 or HTTP proxies — with built-in support for **XRay VLESS+REALITY** tunneling.

This fork extends the original [ProxyBridge](https://github.com/InterceptSuite/ProxyBridge) Windows GUI with a full XRay Reality integration: manage the xray-core subprocess directly from the UI, import server configs via `vless://` share links, and auto-connect on application launch.

---

**ProxyBridge** — лёгкий Windows-клиент с открытым исходным кодом (альтернатива Proxifier), который прозрачно маршрутизирует TCP и UDP трафик выбранных приложений через SOCKS5 или HTTP прокси — со встроенной поддержкой туннелирования **XRay VLESS+REALITY**.

Этот форк расширяет оригинальный [ProxyBridge](https://github.com/InterceptSuite/ProxyBridge) Windows GUI полноценной интеграцией XRay Reality: управление subprocess xray-core прямо из интерфейса, импорт конфигурации сервера по ссылке `vless://`, автоподключение при запуске приложения.

---

## Features

### Routing
- **Process-based routing** — route, block, or allow traffic per application
- **SOCKS5 & HTTP proxy support**
- **Kernel-level interception** via WinDivert
- **Rules engine** — per-process, per-host, per-port, TCP/UDP, wildcards
- **DNS via proxy**
- **Independent start/stop** for routing and XRay modules

### XRay Reality
- **Start/stop xray-core** from the GUI without leaving the app
- **VLESS+REALITY** protocol with SOCKS5 + HTTP inbounds
- **Import from `vless://` URL** — paste a share link to auto-fill all settings
- **Auto-download xray-core** — if the binary is not found, the app offers to download it from GitHub Releases
- **Auto-start on launch** — configurable independently for routing and XRay
- **Binary auto-detection**: configured path → PATH → app directory

### Interface
- Modern dark Avalonia UI (.NET 10)
- **English / Russian** localization
- Status bar with dual indicators for routing and XRay
- Traffic and activity log

---

## Возможности

### Маршрутизация
- **Маршрутизация на уровне процессов** — route, block или allow для каждого приложения
- **Поддержка SOCKS5 и HTTP прокси**
- **Перехват на уровне ядра** через WinDivert
- **Система правил** — по процессу, хосту, порту, протоколу TCP/UDP, с поддержкой wildcard
- **DNS через прокси**
- **Независимый запуск/остановка** маршрутизации и XRay

### XRay Reality
- **Запуск/остановка xray-core** прямо из интерфейса
- Протокол **VLESS+REALITY** с SOCKS5 и HTTP inbound
- **Импорт из ссылки `vless://`** — вставьте share-ссылку для автозаполнения настроек
- **Автоматическая загрузка xray-core** — если бинарный файл не найден, приложение предложит скачать его с GitHub Releases
- **Автозапуск при старте** — настраивается отдельно для маршрутизации и XRay
- **Автопоиск бинарного файла**: заданный путь → PATH → директория приложения

### Интерфейс
- Современный тёмный интерфейс на Avalonia UI (.NET 10)
- Локализация на **английском и русском**
- Статус-бар с индикаторами состояния маршрутизации и XRay
- Журнал трафика и активности

---

## Requirements

- **OS**: Windows 10 or later (64-bit)
- **Privileges**: Administrator
- **.NET**: bundled — no separate installation required
- **XRay**: downloaded automatically or provide your own `xray.exe`

---

## Требования

- **ОС**: Windows 10 и новее (64-bit)
- **Права**: администратор
- **.NET**: входит в сборку, отдельная установка не нужна
- **XRay**: скачивается автоматически или укажите свой `xray.exe`

---

## Getting Started

1. Download the latest release from [Releases](https://github.com/Visp1024/ProxyBridgeXRay/releases) and run `ProxyBridge.exe` as Administrator.
2. Open **Proxy → XRay Reality Settings** and paste your `vless://` link into the Import field, or fill in the fields manually.
3. Click **Start XRay** from the Proxy menu. ProxyBridge will automatically route traffic through the local XRay SOCKS5 port.
4. To enable auto-connect: check **Auto-start XRay on Launch** in the Proxy menu or in XRay Settings.

---

## Начало работы

1. Скачайте последний релиз из [Releases](https://github.com/Visp1024/ProxyBridgeXRay/releases) и запустите `ProxyBridge.exe` от имени администратора.
2. Откройте **Proxy → XRay Reality Settings** и вставьте ссылку `vless://` в поле импорта, или заполните поля вручную.
3. Нажмите **Start XRay** в меню Proxy. ProxyBridge автоматически начнёт маршрутизировать трафик через локальный SOCKS5 порт XRay.
4. Для автоподключения при старте: включите **Auto-start XRay on Launch** в меню Proxy или в настройках XRay.

---

## Screenshots

<p align="center">
  <img src="img/ProxyBridge.png" alt="ProxyBridge Windows Main Interface" width="800"/>
  <br/>
  <em>Main Interface</em>
</p>

<p align="center">
  <img src="img/proxy-setting.png" alt="Proxy Settings" width="800"/>
  <br/>
  <em>Proxy Settings</em>
</p>

<p align="center">
  <img src="img/proxy-rule.png" alt="Proxy Rules" width="800"/>
  <br/>
  <em>Proxy Rules</em>
</p>

<p align="center">
  <img src="img/proxy-rule2.png" alt="Add Rule" width="800"/>
  <br/>
  <em>Add Rule</em>
</p>

<p align="center">
  <img src="img/Vless settings.png" alt="XRay VLESS+Reality Settings" width="800"/>
  <br/>
  <em>XRay VLESS+Reality Settings</em>
</p>

---

## License

MIT License — see [LICENSE](LICENSE) for details.

---

## Credits

- [WinDivert](https://reqrypt.org/windivert.html) by basil00 — kernel-level packet interception
- [Avalonia UI](https://avaloniaui.net/) — cross-platform .NET UI framework
- [xray-core](https://github.com/XTLS/Xray-core) — VLESS+REALITY proxy core
- Original [ProxyBridge](https://github.com/InterceptSuite/ProxyBridge) by Sourav Kalal / InterceptSuite