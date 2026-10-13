# Как выложить проект на GitHub

Git на этом компьютере не установлен, поэтому команды ниже нужно выполнить самому.
Проект уже подготовлен: есть `.gitignore`, `README.md`, `LICENSE` и `Build.ps1`.

---

## Шаг 1. Установить Git

Скачать и установить: <https://git-scm.com/download/win>

Параметры установки можно оставить по умолчанию. После установки **закрыть и заново
открыть** PowerShell, иначе команда `git` не появится.

Проверка:

```powershell
git --version
```

Должно вывести что-то вроде `git version 2.47.0.windows.1`.

Заодно представиться (это попадёт в историю коммитов):

```powershell
git config --global user.name "Ваше Имя"
git config --global user.email "ваш@email.com"
```

---

## Шаг 2. Создать репозиторий на GitHub

1. Открыть <https://github.com/new>
2. **Repository name**: например `dynamic-island-windows`
3. Поставить **Public** (или Private, если хотите скрыть)
4. **Не** ставить галочки «Add a README file», «Add .gitignore», «Choose a license» —
   всё это уже есть в проекте, иначе будет конфликт
5. Нажать **Create repository**

GitHub покажет страницу с командами. Нужен адрес вида
`https://github.com/ВАШ_ЛОГИН/dynamic-island-windows.git`.

---

## Шаг 3. Создать локальный репозиторий и закоммитить

Выполнить по порядку (замените путь, если папка лежит в другом месте):

```powershell
cd "C:\Users\Arch\Documents\deepseek-harness\default-workspace\DynamicIsland"

git init
git add .
git status              # проверить, что попадает в коммит
git commit -m "Dynamic Island for Windows 11: layered-window pill with live activities"
```

Обратите внимание на `git status`: в списке **не должно быть** `DynamicIsland.exe`,
`settings.ini` и `island-status.txt` — они исключены через `.gitignore`.

Если exe всё-таки попал в список (например, был добавлен раньше), убрать из индекса:

```powershell
git rm --cached DynamicIsland.exe
```

---

## Шаг 4. Загрузить на GitHub

```powershell
git branch -M main
git remote add origin https://github.com/ВАШ_ЛОГИН/dynamic-island-windows.git
git push -u origin main
```

При первом `push` откроется окно входа в GitHub — нужно авторизоваться.
Пароль от аккаунта **не подойдёт**, нужен Personal Access Token:
<https://github.com/settings/tokens> → *Generate new token (classic)* → галочка `repo`.

Проще: установить GitHub CLI и войти через браузер — тогда токен не нужен:

```powershell
winget install --id GitHub.cli
gh auth login
```

---

## Дальнейшие изменения

После правок в файлах:

```powershell
git add .
git commit -m "кратко что изменилось"
git push
```

---

## Что уже исключено из репозитория

Файл `.gitignore` не даёт попасть в коммит:

| Исключено | Почему |
|---|---|
| `DynamicIsland.exe` | собирается из исходников командой `Build.ps1` |
| `settings.ini` | личные настройки (ширина острова) |
| `island-status.txt` | рабочий файл, создаётся во время работы |
| `bin/`, `obj/`, `*.pdb` | артефакты сборки |

Снимки из `shots/` **остаются** — они используются в README как иллюстрации.

---

## Если хотите выложить заодно HTML-плеер

В папке выше лежит `liquid-glass-player.html` (отдельный проект — музыкальный плеер
в стиле liquid glass). Можно сделать один репозиторий с двумя папками:

```
dynamic-island-windows/
├── README.md          # общий, с описанием обоих проектов
├── LICENSE
├── DynamicIsland/     # остров
└── LiquidGlassPlayer/
    └── index.html     # плеер (переименованный liquid-glass-player.html)
```

Тогда инициализировать репозиторий нужно на уровень выше — в
`C:\Users\Arch\Documents\deepseek-harness\default-workspace`.

Чтобы GitHub Pages отдавал плеер как страницу, положите его как `index.html`
в корень репозитория и включите *Settings → Pages → Deploy from branch → main / root*.
