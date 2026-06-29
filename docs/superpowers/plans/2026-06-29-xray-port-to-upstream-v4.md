# Перенос XRay-форка на апстрим ProxyBridge v4.0.0 — план миграции

> **For agentic workers:** REQUIRED SUB-SKILL: используйте superpowers:subagent-driven-development или superpowers:executing-plans для выполнения этого плана по задачам. Шаги используют чекбоксы (`- [ ]`).

**Goal:** Перенести функциональность форка (XRay VLESS+Reality, русская локализация, тихий автозапуск, Windows-only брендинг/инсталлятор) на свежую базу апстрима `upstream/master` (v4.0.0) методом чистой пересборки.

**Architecture:** Берём `upstream/master` как новую базу новой ветки. Удаляем не-Windows части (Linux/macOS/CLI). Реализуем XRay-интеграцию идиоматично под новую архитектуру профилей v4: XRay — это локальный SOCKS5-прокси, представленный **управляемым прокси-конфигом** в активном профиле; жизненный цикл бинарника управляется отдельным меню «XRay». Настройки XRay и флаг автозапуска хранятся на уровне приложения (`AppSettings` / `settings.json`), отдельно от профилей.

**Tech Stack:** .NET 10.0 (`net10.0-windows`), Avalonia 11.3.11 (Fluent), AOT + Trimmed publish, System.Text.Json source-generation, нативный `ProxyBridgeCore.dll` через P/Invoke, NSIS-инсталлятор, GitHub Actions.

## Global Constraints

- **Только Windows.** Linux/, MacOS/, и не-Windows workflow'ы должны быть удалены. XRay-функциональность — Windows-only.
- **Целевой фреймворк:** `net10.0-windows`, `PublishAot=true`, `PublishTrimmed=true`. ⚠️ Из-за AOT/trimming **вся JSON-сериализация обязана идти через source-generated `JsonSerializerContext`** — рефлексивный `JsonSerializer` сломается в trimmed-сборке.
- **Namespace:** `ProxyBridge.GUI.*` (Services / ViewModels / Views / Interop / Resources).
- **Локализация:** механизм апстрима — `Loc.Instance` (singleton) + `Resources.Resources.Culture`. Языки в `ProxyProfile.Language` (per-profile). Добавляем `ru` к существующим `en`/`zh`. Новые строки добавляются в `Resources.resx` (en), `Resources.zh.resx` (zh) и новый `Resources.ru.resx`.
- **Атомарная запись:** все JSON-файлы пишутся через `AtomicFileHelper.AtomicWrite`.
- **Источник исходного кода форка:** оригинальные файлы форка доступны как `git show master:<путь>`; интеграционные диффы — `git diff 51d9b17 master -- <путь>`. НЕ копировать вслепую — адаптировать под v4.
- **Базовый ref апстрима:** `upstream/master` (= тег `v4.0.0`). Точка расхождения форка: `51d9b17`.

---

## Ключевое архитектурное решение (XRay в модели v4)

В форке (v3.2) был **один** прокси; старт XRay подменял его на `127.0.0.1:10808` и восстанавливал при остановке. В v4 прокси **несколько**, правила ссылаются на `ProxyConfigId`, а роутинг **всегда автозапускается** в `SetMainWindow` (нет ручного Start/Stop). Поэтому:

- **Хак save/restore single-proxy НЕ переносится.** Вместо него XRay представляется управляемым прокси-конфигом.
- **Управляемый конфиг «XRay Reality»:** при старте XRay в активный профиль гарантированно добавляется/обновляется `ProxyConfigEntry` (Type=`SOCKS5`, Host=`127.0.0.1`, Port=`LocalPort`) и регистрируется в нативном ядре. Пользователь нацеливает правила на него через штатный UI «Proxy Rules».
- **Меню «XRay»** (новый top-level пункт): Reality Settings…, Start XRay, Stop XRay (видимость по состоянию). Если бинарник отсутствует — открывается окно загрузки.
- **Хранение:** `XRayConfig` + `AutoStartXRay` кладём в `AppSettings` (глобально, `settings.json`), т.к. это единый туннель, не привязанный к профилю.
- **Тихий автозапуск форка** (`--minimized` → полностью скрыть окно) переносится в `App.axaml.cs`. Концепция отдельного `AutoStartRouting` **не нужна** (роутинг в v4 и так всегда включён); остаётся только `AutoStartXRay`.

> ⚠️ **Решение, требующее подтверждения пользователя перед Задачей 6.** UX-отличие от форка: правила теперь явно ссылаются на конфиг «XRay Reality» (свойство модели v4), а не на неявный единственный прокси. Альтернатива — авто-навешивать catch-all PROXY-правило при старте XRay (ближе к форку, но навязчиво). Рекомендуется первый вариант.

---

## Структура файлов

**Создаются (порт из форка, адаптированы):**
- `Windows/gui/Services/XRayService.cs` — жизненный цикл процесса xray + генерация JSON-конфига VLESS+Reality.
- `Windows/gui/Services/XRayDownloadService.cs` — загрузка xray-core из GitHub Releases.
- `Windows/gui/ViewModels/XRaySettingsViewModel.cs` — VM окна настроек.
- `Windows/gui/ViewModels/XRayDownloadViewModel.cs` — VM окна загрузки.
- `Windows/gui/Views/XRaySettingsWindow.axaml` (+ `.axaml.cs`) — окно настроек.
- `Windows/gui/Views/XRayDownloadWindow.axaml` (+ `.axaml.cs`) — окно загрузки.
- `Windows/gui/Resources/Resources.ru.resx` — русская локализация (полный набор ключей en + XRay).
- `Windows/gui/Models/XRayConfig.cs` — модель конфига XRay (выносим из старого ConfigManager).

**Модифицируются (апстрим v4):**
- `Windows/gui/Services/SettingsService.cs` — `AppSettings`: добавить `XRayConfig XRay`, `bool AutoStartXRay`; зарегистрировать в `AppSettingsContext`.
- `Windows/gui/Services/Loc.cs` — добавить XRay-свойства.
- `Windows/gui/ViewModels/MainWindowViewModel.cs` — XRay-сервис, команды, статус, управляемый конфиг, автозапуск, чекмарк `ru`.
- `Windows/gui/Views/MainWindow.axaml` — меню «XRay», статус-бар XRay, пункт языка «Русский».
- `Windows/gui/Views/MainWindow.axaml.cs` — `OnChangeLanguageRussian`.
- `Windows/gui/App.axaml.cs` — скрытие окна при `--minimized`.
- `Windows/gui/ProxyBridge.GUI.csproj` — `EmbeddedResource` для `Resources.ru.resx`.
- `Windows/gui/Resources/Resources.resx` + `Resources.zh.resx` — XRay-ключи.
- `Windows/installer/ProxyBridge.nsi`, `.github/workflows/*`, `README.md`, `img/*` — брендинг/CI/Windows-only.

**Удаляются:** `Linux/`, `MacOS/`, `.github/workflows/build-linux.yml`, `build-mac.yml`, `release-linux.yml`, `release-mac.yml`, и (по решению Windows-only) новый апстримный C-CLI `Windows/cli/` если он не нужен форку.

> **Замечание о тестировании.** Это десктоп-GUI на Avalonia поверх нативного DLL — юнит-тест-харнесса в проекте нет, классический TDD неприменим. «Зелёный» критерий каждой задачи — **успешная сборка `dotnet build`** соответствующего проекта (где применимо) и **ручной smoke-тест** на финале. Где можно изолировать чистую логику (парсер vless://, генерация JSON), добавляем точечную проверку через временный консольный прогон.

---

## Task 0: Подготовка ветки и фиксация базы

**Files:** только git-операции.

**Interfaces:**
- Produces: ветка `feat/xray-on-v4` с состоянием `upstream/master` (v4.0.0); тег-ориентир для отката.

- [ ] **Step 1: Убедиться, что рабочее дерево чистое и зафиксировать текущий master как точку отката**

```bash
git status --porcelain        # должно быть пусто (кроме Windows/gui/.idea/ — это игнор)
git tag fork-v3-snapshot master   # ориентир на старую версию форка
```

- [ ] **Step 2: Создать рабочую ветку от апстрима v4.0.0**

```bash
git fetch upstream
git switch -c feat/xray-on-v4 upstream/master
git log -1 --oneline          # ожидаем VERSION 4.0.0 (a898928 или 8a0c631)
```

- [ ] **Step 3: Зафиксировать стартовую точку (пустой коммит-маркер)**

```bash
git commit --allow-empty -m "chore: start XRay port onto upstream v4.0.0

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 1: Windows-only — удалить не-Windows части

**Files:**
- Delete: `Linux/` (вся), `MacOS/` (вся)
- Delete: `.github/workflows/build-linux.yml`, `build-mac.yml`, `release-linux.yml`, `release-mac.yml`
- Delete (Windows-only решение): `Windows/cli/` (новый C-CLI апстрима) — **только если** форку CLI не нужен (он не использовался). Иначе оставить.
- Delete: не-Windows скриншоты `img/ProxyBridge-linux.png`, `ProxyBridge-mac.png`, `ProxyBridge_CLI-linux.png`, `proxy-rule-linux.png`, `proxy-rule-mac.png`, `proxy-rule2-linux.png`, `proxy-rule2-mac.png`, `proxy-setting-linux.png`, `proxy-setting-mac.png`, `flow.png`, `flow1.png` (сверить с тем, что было удалено в форке: `git show master --stat | grep img`).

**Interfaces:**
- Produces: дерево без Linux/macOS; CI-workflow'ы только Windows.

- [ ] **Step 1: Удалить платформенные каталоги и workflow'ы**

```bash
git rm -r Linux MacOS
git rm .github/workflows/build-linux.yml .github/workflows/build-mac.yml \
       .github/workflows/release-linux.yml .github/workflows/release-mac.yml
```

- [ ] **Step 2: Решить судьбу нового C-CLI апстрима**

```bash
ls Windows/cli   # осмотреть; форк CLI не использовал
# Если оставляем Windows-only без CLI:
git rm -r Windows/cli
```

(Если CLI решено оставить — пропустить удаление и убедиться, что он Windows-only.)

- [ ] **Step 3: Удалить не-Windows изображения**

```bash
git rm "img/ProxyBridge-linux.png" "img/ProxyBridge-mac.png" "img/ProxyBridge_CLI-linux.png" \
       "img/proxy-rule-linux.png" "img/proxy-rule-mac.png" "img/proxy-rule2-linux.png" \
       "img/proxy-rule2-mac.png" "img/proxy-setting-linux.png" "img/proxy-setting-mac.png" 2>/dev/null
git rm "img/flow.png" "img/flow1.png" 2>/dev/null; true
```

- [ ] **Step 4: Проверить, что GUI-проект всё ещё собирается на голой базе v4**

Run: `cd Windows/gui && dotnet build -c Debug`
Expected: BUILD SUCCEEDED (на чистом апстриме v4 без наших правок).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "chore: drop Linux/macOS, keep Windows GUI only

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: Модель XRayConfig + хранение в AppSettings

**Files:**
- Create: `Windows/gui/Models/XRayConfig.cs`
- Modify: `Windows/gui/Services/SettingsService.cs` (класс `AppSettings`: +2 поля; `AppSettingsContext`: +регистрации)

**Interfaces:**
- Produces:
  - `ProxyBridge.GUI.Models.XRayConfig` — POCO с полями (string) `ServerAddress, ServerPort="443", Uuid, Flow="xtls-rprx-vision", Sni, Fingerprint="chrome", PublicKey, ShortId, SpiderX, LocalPort="10808", HttpPort="10809", XRayPath`; (bool) `AutoStartXRay=false`.
  - `AppSettings.XRay : XRayConfig` (default `new()`), `AppSettings.AutoStartXRay` — **не дублировать**: флаг живёт внутри `XRayConfig.AutoStartXRay`. В `AppSettings` добавляем только `public XRayConfig XRay { get; set; } = new();`.
- Consumes: ничего.

- [ ] **Step 1: Создать модель `Windows/gui/Models/XRayConfig.cs`**

Источник полей — `git show master:Windows/gui/Services/ConfigManager.cs` (класс `XRayConfig`). Перенести как отдельный файл:

```csharp
namespace ProxyBridge.GUI.Models;

public class XRayConfig
{
    public string ServerAddress { get; set; } = "";
    public string ServerPort    { get; set; } = "443";
    public string Uuid          { get; set; } = "";
    public string Flow          { get; set; } = "xtls-rprx-vision";
    public string Sni           { get; set; } = "";
    public string Fingerprint   { get; set; } = "chrome";
    public string PublicKey     { get; set; } = "";
    public string ShortId       { get; set; } = "";
    public string SpiderX       { get; set; } = "";
    public string LocalPort     { get; set; } = "10808";
    public string HttpPort      { get; set; } = "10809";
    public string XRayPath      { get; set; } = "";
    public bool   AutoStartXRay { get; set; } = false;
}
```

- [ ] **Step 2: Подключить XRay в `AppSettings` и в JSON-контекст**

В `Windows/gui/Services/SettingsService.cs`:
- В начало файла добавить `using ProxyBridge.GUI.Models;`
- В класс `AppSettings` добавить поле: `public XRayConfig XRay { get; set; } = new();`
- Над `AppSettingsContext` добавить атрибут: `[JsonSerializable(typeof(XRayConfig))]`

```csharp
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(XRayConfig))]
internal partial class AppSettingsContext : JsonSerializerContext { }
```

- [ ] **Step 3: Сборка**

Run: `cd Windows/gui && dotnet build -c Debug`
Expected: BUILD SUCCEEDED.

- [ ] **Step 4: Commit**

```bash
git add Windows/gui/Models/XRayConfig.cs Windows/gui/Services/SettingsService.cs
git commit -m "feat: add XRayConfig model and store it in AppSettings

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: Портировать XRay-сервисы (процесс + загрузка)

**Files:**
- Create: `Windows/gui/Services/XRayService.cs` (из `git show master:Windows/gui/Services/XRayService.cs`)
- Create: `Windows/gui/Services/XRayDownloadService.cs` (из `git show master:Windows/gui/Services/XRayDownloadService.cs`)

**Interfaces:**
- Consumes: `ProxyBridge.GUI.Models.XRayConfig` (Task 2).
- Produces:
  - `XRayService`: `bool Start(XRayConfig cfg)`, `void Stop()`, `static string? FindXRayExecutable(string configuredPath)`; события `event Action<string> LogReceived`, `event Action Started`, `event Action<int> Stopped`.
  - `XRayDownloadService`: `static Task<string> DownloadAsync(IProgress<(int Percent, string Status)> progress, CancellationToken ct)`.

- [ ] **Step 1: Вытащить оригинальные файлы из ветки форка**

```bash
git show fork-v3-snapshot:Windows/gui/Services/XRayService.cs > Windows/gui/Services/XRayService.cs
git show fork-v3-snapshot:Windows/gui/Services/XRayDownloadService.cs > Windows/gui/Services/XRayDownloadService.cs
```

- [ ] **Step 2: Проверить namespace и зависимости**

Открыть оба файла. Убедиться: namespace `ProxyBridge.GUI.Services`; тип конфига — `ProxyBridge.GUI.Models.XRayConfig` (добавить `using ProxyBridge.GUI.Models;` если в оригинале был другой namespace `XRayConfig`). Убедиться, что JSON-конфиг XRay строится **строкой/JsonNode**, а не рефлексивным сериализатором (AOT). Если используется `JsonSerializer.Serialize` по reflection — переписать на ручную сборку строки или source-gen контекст.

- [ ] **Step 3: Сборка**

Run: `cd Windows/gui && dotnet build -c Debug`
Expected: BUILD SUCCEEDED.

- [ ] **Step 4: Commit**

```bash
git add Windows/gui/Services/XRayService.cs Windows/gui/Services/XRayDownloadService.cs
git commit -m "feat: port XRay process and download services

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: Локализация — XRay-ключи + русский язык

**Files:**
- Modify: `Windows/gui/Resources/Resources.resx` (en — добавить XRay/Routing-ключи)
- Modify: `Windows/gui/Resources/Resources.zh.resx` (zh — те же ключи, перевод или временно англ.)
- Create: `Windows/gui/Resources/Resources.ru.resx` (полный набор: все ключи из en + XRay)
- Modify: `Windows/gui/Services/Loc.cs` (добавить XRay-свойства)
- Modify: `Windows/gui/ProxyBridge.GUI.csproj` (`EmbeddedResource` для `Resources.ru.resx`)

**Interfaces:**
- Consumes: механизм `Loc` апстрима.
- Produces: новые свойства `Loc`: `MenuXRay`, `MenuXRaySettings`, `MenuStartXRay`, `MenuStopXRay`, `WindowXRaySettings`, `XRaySubtitle`, `XRaySectionImport/Server/Auth/Reality/Inbounds/Binary/AutoStart`, `LabelServerAddress/Flow/Sni/Fingerprint/PublicKey/ShortId/SpiderX/Socks5Port/HttpPort/XRayPath`, `Placeholder*`, `XRay*Hint`, `XRayDownloadTitle/Subtitle`, `ButtonImport/Retry/SaveSettings` и т.д.

> Полный список XRay-ключей и их английские значения — в `git show fork-v3-snapshot:Windows/gui/Resources/Resources.resx` (diff: `git diff 51d9b17 fork-v3-snapshot -- Windows/gui/Resources/Resources.resx`). Свойства `Loc` — в `git show fork-v3-snapshot:Windows/gui/Services/Loc.cs`.

- [ ] **Step 1: Собрать список XRay-ключей форка**

```bash
git diff 51d9b17 fork-v3-snapshot -- Windows/gui/Resources/Resources.resx > /tmp/xray-resx.diff
git show fork-v3-snapshot:Windows/gui/Services/Loc.cs > /tmp/fork-loc.cs
```

Из diff'а выделить **только** XRay/Routing-ключи (не трогать ключи, конфликтующие с уже существующими в v4 — у v4 свой набор Menu*/Label*).

- [ ] **Step 2: Добавить XRay-ключи в `Resources.resx` (en)**

Вставить `<data name="...">` для каждого XRay-ключа со значением из форка. НЕ дублировать существующие в v4 имена. Пропустить ключ `MenuAutoStartXRay` (в форке удалён из меню; не нужен).

- [ ] **Step 3: Добавить те же ключи в `Resources.zh.resx`**

Скопировать те же `name`, значения — китайский перевод (или временно английский с пометкой; апстрим-мейнтейнеры дополнят). Главное — комплект ключей идентичен en, иначе при zh-локали будет fallback на ключ.

- [ ] **Step 4: Создать `Resources.ru.resx`**

Полный русский комплект: взять ВСЕ `name` из `Resources.resx` (en) + XRay-ключи, перевести значения на русский. Базу русских значений взять из `git show fork-v3-snapshot:Windows/gui/Resources/Resources.ru.resx`, дополнив ключами, появившимися в v4 (Profile*, LogFilters*, Tab*, и т.п.). Сохранить корректную типографику (кавычки, тире).

- [ ] **Step 5: Расширить `Loc.cs` XRay-свойствами**

Добавить в `Windows/gui/Services/Loc.cs` блок свойств (по образцу `git show fork-v3-snapshot:Windows/gui/Services/Loc.cs`), напр.:

```csharp
    // XRay
    public string MenuXRay          => Resources.Resources.MenuXRay;
    public string MenuXRaySettings  => Resources.Resources.MenuXRaySettings;
    public string MenuStartXRay     => Resources.Resources.MenuStartXRay;
    public string MenuStopXRay      => Resources.Resources.MenuStopXRay;
    public string WindowXRaySettings=> Resources.Resources.WindowXRaySettings;
    public string XRaySubtitle      => Resources.Resources.XRaySubtitle;
    // ...остальные XRay-ключи (секции, метки, плейсхолдеры, подсказки, download)
```

Добавлять только свойства, которых ещё нет в апстрим-`Loc.cs`.

- [ ] **Step 6: Зарегистрировать `Resources.ru.resx` в csproj**

В `Windows/gui/ProxyBridge.GUI.csproj` рядом с `Resources.zh.resx`:

```xml
<EmbeddedResource Update="Resources\Resources.ru.resx">
  <DependentUpon>Resources.resx</DependentUpon>
</EmbeddedResource>
```

- [ ] **Step 7: Сборка (генерируется Resources.Designer.cs)**

Run: `cd Windows/gui && dotnet build -c Debug`
Expected: BUILD SUCCEEDED, новые свойства `Resources.*` доступны.

- [ ] **Step 8: Commit**

```bash
git add Windows/gui/Resources/ Windows/gui/Services/Loc.cs Windows/gui/ProxyBridge.GUI.csproj
git commit -m "feat: add XRay localization keys and Russian language

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 5: Портировать окна XRay (Settings + Download)

**Files:**
- Create: `Windows/gui/ViewModels/XRaySettingsViewModel.cs`, `XRayDownloadViewModel.cs`
- Create: `Windows/gui/Views/XRaySettingsWindow.axaml` (+ `.axaml.cs`), `XRayDownloadWindow.axaml` (+ `.axaml.cs`)

**Interfaces:**
- Consumes: `XRayConfig` (Task 2), `XRayService`/`XRayDownloadService` (Task 3), `Loc` (Task 4).
- Produces:
  - `XRaySettingsViewModel(XRayConfig current, Action<XRayConfig> onSave, Action closeWindow)` (сверить фактическую сигнатуру конструктора с форком) — свойства полей, `SaveCommand`, `CancelCommand`, `ImportFromUrlCommand`; парсер `TryParseVlessUrl`.
  - `XRayDownloadViewModel`: `Task StartAsync(Action closeWindow)`, свойства Progress/StatusText/IsRunning/IsSuccess/IsFailed/ErrorText/DownloadedPath, `CancelCommand`.

- [ ] **Step 1: Вытащить файлы из форка**

```bash
for f in \
  ViewModels/XRaySettingsViewModel.cs ViewModels/XRayDownloadViewModel.cs \
  Views/XRaySettingsWindow.axaml Views/XRaySettingsWindow.axaml.cs \
  Views/XRayDownloadWindow.axaml Views/XRayDownloadWindow.axaml.cs ; do
  git show "fork-v3-snapshot:Windows/gui/$f" > "Windows/gui/$f"
done
```

- [ ] **Step 2: Выверить namespace/usings и тип конфига**

Во всех 6 файлах: namespace `ProxyBridge.GUI.ViewModels` / `ProxyBridge.GUI.Views`; `using ProxyBridge.GUI.Models;` для `XRayConfig`; `x:Class` в axaml соответствует namespace. Биндинги `{Binding Loc.*}` — проверить, что используют свойства, добавленные в Task 4.

- [ ] **Step 3: Сборка**

Run: `cd Windows/gui && dotnet build -c Debug`
Expected: BUILD SUCCEEDED.

- [ ] **Step 4: Точечная проверка парсера vless:// (изолированная логика)**

Если возможно без запуска GUI — временно вызвать `TryParseVlessUrl` на тестовой ссылке `vless://uuid@host:443?security=reality&pbk=KEY&flow=xtls-rprx-vision&sni=example.com&fp=chrome&sid=abcd&spx=%2F#name` через временный xunit-подобный прогон или `dotnet run` в одноразовом скрипте. Убедиться, что поля заполняются. Удалить временный прогон после проверки.

- [ ] **Step 5: Commit**

```bash
git add Windows/gui/ViewModels/XRay*.cs Windows/gui/Views/XRay*.axaml*
git commit -m "feat: port XRay settings and download windows

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 6: Интеграция XRay в MainWindowViewModel + MainWindow

> ⚠️ **Перед началом — подтвердить архитектурное решение** из раздела «Ключевое архитектурное решение». Эта задача реализует управляемый прокси-конфиг «XRay Reality», меню «XRay», статус-бар и автозапуск.

**Files:**
- Modify: `Windows/gui/ViewModels/MainWindowViewModel.cs`
- Modify: `Windows/gui/Views/MainWindow.axaml`
- Modify: `Windows/gui/Views/MainWindow.axaml.cs`
- Modify: `Windows/gui/App.axaml.cs`

**Interfaces:**
- Consumes: `XRayService` (Task 3), `XRayConfig`/`AppSettings.XRay` (Task 2), `XRaySettingsWindow`/`XRayDownloadWindow` (Task 5), `ProxyBridgeService.AddProxyConfig/DeleteProxyConfig` (v4), `ProfileManager`/`SwitchToProfile` (v4), `Loc` (Task 4).
- Produces (новые члены `MainWindowViewModel`):
  - поля: `_xRayService`, `_xRayConfig` (из `AppSettings.XRay`), `_isXRayRunning`, `_xRayStatusText`, `uint _xRayManagedConfigId`.
  - свойства: `bool IsXRayRunning`, `bool IsXRayStopped => !_isXRayRunning`, `string XRayStatusText`, `string RussianCheckmark`.
  - команды: `ShowXRaySettingsCommand`, `StartXRayCommand`, `StopXRayCommand`.
  - методы: `Task TryAutoStartXRayAsync()`, `bool EnsureXRayProxyConfig()` (добавляет/обновляет управляемый конфиг в `ProxyConfigs` + native), `void RemoveXRayProxyConfig()`.

**Реализация (адаптация форка под v4):**

- [ ] **Step 1: Поля и свойства XRay в MainWindowViewModel**

Добавить поля (рядом с `_proxyService`):

```csharp
private readonly Services.XRayService _xRayService = new();
private Models.XRayConfig _xRayConfig = new();
private bool _isXRayRunning;
private string _xRayStatusText = "XRay: Stopped";
private uint _xRayManagedConfigId; // native id управляемого SOCKS5-конфига
private string _russianCheckmark = "";
```

И свойства с `OnPropertyChanged` (по образцу `EnglishCheckmark`/`ChineseCheckmark` в v4):

```csharp
public bool IsXRayRunning { get => _isXRayRunning; private set { _isXRayRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsXRayStopped)); } }
public bool IsXRayStopped => !_isXRayRunning;
public string XRayStatusText { get => _xRayStatusText; private set { _xRayStatusText = value; OnPropertyChanged(); } }
public string RussianCheckmark { get => _russianCheckmark; private set { _russianCheckmark = value; OnPropertyChanged(); } }
```

- [ ] **Step 2: Загрузка XRayConfig из настроек + подписка на события сервиса**

В `LoadConfiguration()` (после `var settings = _settingsService.LoadSettings();`) добавить `_xRayConfig = settings.XRay ?? new();`. В обновлении чекмарков языка добавить `RussianCheckmark = profile.Language == "ru" ? "✓" : "";`.

В `SetMainWindow` (после создания `_proxyService`, до/после таймеров) подписать события XRay:

```csharp
_xRayService.LogReceived += msg =>
{
    lock (_activityLogLock) _pendingActivityLogs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
};
_xRayService.Stopped += _ => Dispatcher.UIThread.Post(() =>
{
    IsXRayRunning = false;
    XRayStatusText = "XRay: Stopped";
    RemoveXRayProxyConfig();
});
```

- [ ] **Step 3: Метод управляемого конфига `EnsureXRayProxyConfig` / `RemoveXRayProxyConfig`**

Идиоматично v4: регистрируем SOCKS5 `127.0.0.1:LocalPort` в native и добавляем в `ProxyConfigs` (чтобы был виден в Proxy Rules). Конфиг помечаем по host/port.

```csharp
private const string XRayConfigHost = "127.0.0.1";

private bool EnsureXRayProxyConfig()
{
    if (_proxyService == null) return false;
    if (!ushort.TryParse(_xRayConfig.LocalPort, out var port)) return false;

    // уже есть в коллекции?
    var existing = ProxyConfigs.FirstOrDefault(p =>
        p.Host == XRayConfigHost && p.Port == _xRayConfig.LocalPort && p.Type == "SOCKS5");
    if (existing != null) { _xRayManagedConfigId = existing.Id; return true; }

    uint nativeId = _proxyService.AddProxyConfig("SOCKS5", XRayConfigHost, port, "", "");
    if (nativeId == 0) return false;
    _xRayManagedConfigId = nativeId;
    ProxyConfigs.Add(new ProxyConfig { Id = nativeId, Type = "SOCKS5", Host = XRayConfigHost, Port = _xRayConfig.LocalPort });
    SaveCurrentProfile();
    return true;
}

private void RemoveXRayProxyConfig()
{
    if (_proxyService == null || _xRayManagedConfigId == 0) return;
    var pc = ProxyConfigs.FirstOrDefault(p => p.Id == _xRayManagedConfigId);
    if (pc != null) { ProxyConfigs.Remove(pc); _proxyService.DeleteProxyConfig(_xRayManagedConfigId); SaveCurrentProfile(); }
    _xRayManagedConfigId = 0;
}
```

> Сверить точные имена `ProxyConfig`/`SaveCurrentProfile`/`SaveCurrentProfileAsync` с фактическим v4-кодом (`MainWindowViewModel.cs`) перед написанием.

- [ ] **Step 4: Команды Start/Stop/Settings XRay**

```csharp
public ICommand ShowXRaySettingsCommand { get; }
public ICommand StartXRayCommand { get; }
public ICommand StopXRayCommand { get; }
```

Инициализация в конструкторе:
- `ShowXRaySettingsCommand` → открыть `XRaySettingsWindow` модально с копией `_xRayConfig`; в `onSave` обновить `_xRayConfig`, сохранить в `AppSettings.XRay` через `_settingsService` (Load→mutate→Save).
- `StartXRayCommand` → если `XRayService.FindXRayExecutable(_xRayConfig.XRayPath) == null`, открыть `XRayDownloadWindow`, по успеху записать путь в `_xRayConfig.XRayPath` и сохранить; затем `_xRayService.Start(_xRayConfig)`; при успехе `IsXRayRunning = true; XRayStatusText = $"XRay: Running · SOCKS5 :{_xRayConfig.LocalPort}"; EnsureXRayProxyConfig();`.
- `StopXRayCommand` → `_xRayService.Stop(); IsXRayRunning = false; XRayStatusText = "XRay: Stopped"; RemoveXRayProxyConfig();`.

- [ ] **Step 5: Автозапуск XRay при старте**

В конце `SetMainWindow` (после `_ = CheckForUpdatesOnStartupAsync();`):

```csharp
if (_xRayConfig.AutoStartXRay) _ = TryAutoStartXRayAsync();
```

Метод:

```csharp
private async Task TryAutoStartXRayAsync()
{
    await Task.Delay(800);
    if (string.IsNullOrWhiteSpace(_xRayConfig.ServerAddress) ||
        string.IsNullOrWhiteSpace(_xRayConfig.Uuid) ||
        string.IsNullOrWhiteSpace(_xRayConfig.PublicKey)) return;
    if (Services.XRayService.FindXRayExecutable(_xRayConfig.XRayPath) == null)
    { QueueActivityLog("XRay auto-start skipped: binary not found"); return; }

    await Dispatcher.UIThread.InvokeAsync(() =>
    {
        if (_xRayService.Start(_xRayConfig))
        {
            IsXRayRunning = true;
            XRayStatusText = $"XRay: Running · SOCKS5 :{_xRayConfig.LocalPort}";
            EnsureXRayProxyConfig();
        }
    });
}
```

- [ ] **Step 6: Cleanup и SwitchToProfile**

В `Cleanup()` добавить `try { _xRayService?.Stop(); } catch {}` перед/после dispose `_proxyService`. В `SwitchToProfile` после пересборки конфигов — если XRay запущен, повторно вызвать `EnsureXRayProxyConfig()` (т.к. native-конфиги пересоздаются и id меняется).

- [ ] **Step 7: Меню «XRay» + статус-бар + язык в MainWindow.axaml**

Добавить top-level `<MenuItem Header="{Binding Loc.MenuXRay}">` с подпунктами:
```xml
<MenuItem Header="{Binding Loc.MenuXRaySettings}" Command="{Binding ShowXRaySettingsCommand}"/>
<Separator/>
<MenuItem Header="{Binding Loc.MenuStartXRay}" Command="{Binding StartXRayCommand}" IsVisible="{Binding IsXRayStopped}"/>
<MenuItem Header="{Binding Loc.MenuStopXRay}"  Command="{Binding StopXRayCommand}"  IsVisible="{Binding IsXRayRunning}"/>
```
В меню языка (Settings → Language) добавить пункт «Русский (Russian)» с `Click="OnChangeLanguageRussian"` и иконкой-чекмарком `{Binding RussianCheckmark}` (по образцу English/中文 v4). Добавить XRay-индикатор в статус-бар (если в v4 его нет — добавить нижний `Border` со статусом; иначе встроить `Ellipse`+`TextBlock {Binding XRayStatusText}` с `IsVisible="{Binding IsXRayRunning}"`).

- [ ] **Step 8: `OnChangeLanguageRussian` в MainWindow.axaml.cs**

```csharp
private void OnChangeLanguageRussian(object? sender, RoutedEventArgs e)
{
    if (DataContext is MainWindowViewModel vm) vm.ChangeLanguage("ru");
}
```

Проверить, что `ChangeLanguage("ru")` в v4 корректно ставит культуру и сбрасывает чекмарки (включая `RussianCheckmark`); при необходимости добавить ветку `ru` в `ChangeLanguage`.

- [ ] **Step 9: Тихий автозапуск в App.axaml.cs**

Заменить логику `StartMinimized`: вместо `WindowState=Minimized; ShowInTaskbar=false` — `window.Opened += (s,e) => ((Window)s!).Hide();` (по образцу форка, коммит 13f3590), сверив с фактическим кодом v4 (там запуск `--minimized` уже может обрабатываться — адаптировать, не дублировать).

- [ ] **Step 10: Сборка**

Run: `cd Windows/gui && dotnet build -c Debug`
Expected: BUILD SUCCEEDED.

- [ ] **Step 11: Commit**

```bash
git add Windows/gui/ViewModels/MainWindowViewModel.cs Windows/gui/Views/MainWindow.axaml \
        Windows/gui/Views/MainWindow.axaml.cs Windows/gui/App.axaml.cs
git commit -m "feat: integrate XRay lifecycle, menu, status and autostart into v4 GUI

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 7: Брендинг, инсталлятор, CI, README (Windows-only)

**Files:**
- Modify: `Windows/installer/ProxyBridge.nsi` (имя/иконки/версия — сверить с форком `git diff 51d9b17 fork-v3-snapshot -- Windows/installer/ProxyBridge.nsi`)
- Modify: `.github/workflows/release-windows.yml` (windows-latest, GUI-only, NSIS + portable zip — перенести правки форка)
- Modify: `README.md` (Windows-only, EN/RU, документация XRay Reality — на базе `git show fork-v3-snapshot:README.md`, но поверх нового README v4)
- Replace: лого/иконки `img/logo.png`, `Windows/gui/Assets/logo.ico`, `img/ProxyBridge.png`, добавить `img/Vless settings.png` (из форка)

**Interfaces:** нет кодовых; артефакты сборки/релиза.

- [ ] **Step 1: Перенести брендинг-ассеты из форка**

```bash
git show fork-v3-snapshot:img/logo.png > img/logo.png
git show fork-v3-snapshot:Windows/gui/Assets/logo.ico > Windows/gui/Assets/logo.ico
git show "fork-v3-snapshot:img/Vless settings.png" > "img/Vless settings.png"
git show fork-v3-snapshot:img/ProxyBridge.png > img/ProxyBridge.png
```

- [ ] **Step 2: Перенести правки инсталлятора**

Посмотреть `git diff 51d9b17 fork-v3-snapshot -- Windows/installer/ProxyBridge.nsi` и применить эквивалент поверх версии v4 (имя установщика, иконка, MUI-страницы). Сохранить апстримные изменения v4, наложить только брендинг форка.

- [ ] **Step 3: Перенести release-workflow**

Привести `.github/workflows/release-windows.yml` к Windows-only GUI с NSIS-инсталлятором и portable zip (правки форка: коммиты f5bbc9a, 97accff, 8cfefe2). Сверить с апстрим-версией v4 — не сломать апстримные шаги (CodeQL/SAST оставить).

- [ ] **Step 4: README (Windows-only, EN/RU, XRay)**

Взять новый README v4 как основу, вырезать Linux/macOS-разделы, добавить блок про XRay VLESS+Reality и RU-секцию (из `git show fork-v3-snapshot:README.md`). Обновить скриншоты на актуальные. Указать, что это форк InterceptSuite/ProxyBridge с добавленным XRay.

- [ ] **Step 5: Commit**

```bash
git add Windows/installer/ProxyBridge.nsi .github/workflows/release-windows.yml README.md img/ Windows/gui/Assets/
git commit -m "chore: Windows-only branding, installer, CI and README with XRay docs

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 8: Полная сборка, релизный конфиг и smoke-тест

**Files:** нет изменений (верификация); при находках — точечные фиксы.

- [ ] **Step 1: Release-сборка (AOT/Trimmed) — ловим trimming-ошибки**

Run: `cd Windows/gui && dotnet publish -c Release`
Expected: PUBLISH SUCCEEDED. ⚠️ Если падает на trimming/AOT из-за JSON — вернуться к источнику (XRayService/настройки) и убрать рефлексивную сериализацию.

- [ ] **Step 2: Запуск GUI и ручной smoke-тест**

Запустить собранное приложение и проверить (см. `/run` или `verify` skill):
- открывается, тёмная тема, меню «XRay» присутствует;
- XRay → Reality Settings… открывается, импорт `vless://` заполняет поля, Save сохраняет (проверить `%APPDATA%\ProxyBridge\settings.json` → секция `XRay`);
- Start XRay при отсутствии бинарника предлагает загрузку; после — поднимает процесс, в статус-баре «XRay: Running», в Proxy Rules появляется конфиг `127.0.0.1:<LocalPort>`;
- переключение языка на «Русский» меняет интерфейс, чекмарк выставляется;
- Stop XRay убирает управляемый конфиг и гасит статус;
- профили (New/Switch/Import/Export) работают, XRay-конфиг переживает переключение профиля.

- [ ] **Step 3: Прогнать существующие проверки апстрима**

Если в репозитории есть скрипты сборки/линта апстрима (например `Windows/compile.ps1`) — прогнать, убедиться, что не сломаны.

- [ ] **Step 4: Финальный commit (версия/чистка)**

При необходимости обновить версию приложения (например `4.0.0-xray`) в csproj/installer.

```bash
git add -A
git commit -m "chore: finalize XRay port on v4.0.0; version bump

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

- [ ] **Step 5: Запросить ревью**

Использовать superpowers:requesting-code-review перед слиянием в `master`. Затем — superpowers:finishing-a-development-branch для выбора способа интеграции (merge/PR).

---

## Self-Review (выполнено автором плана)

- **Покрытие правок форка:** XRay-сервисы (T3) ✓, окна/VM (T5) ✓, интеграция/меню/статус/автозапуск (T6) ✓, локализация ru + XRay-ключи (T4) ✓, тихий автозапуск (T6.9) ✓, Windows-only/удаление платформ (T1) ✓, брендинг/инсталлятор/CI/README (T7) ✓, хранение XRay-конфига (T2) ✓.
- **Адаптации под v4 (не дословный перенос):** хак single-proxy save/restore → управляемый прокси-конфиг (T6.3); `AutoStartRouting` упразднён (роутинг v4 всегда on); `ConfigManager` → `AppSettings.XRay` (T2); собственный `Loc.cs` форка → расширение апстрим-`Loc.cs` (T4).
- **Открытые риски:** (1) AOT/trimming + JSON в XRayService (контроль в T3.2/T8.1); (2) точные имена `ProxyConfig`/`SaveCurrentProfile`/конструктора `XRaySettingsViewModel` — сверять с фактическим v4-кодом перед написанием (помечено в T5/T6); (3) UX-решение по правилам (управляемый конфиг vs catch-all) — подтвердить перед T6.
- **Тестирование:** TDD неприменим (GUI+native, нет харнесса) — критерий = сборка + ручной smoke (T8); изолированная проверка парсера vless:// в T5.4.
