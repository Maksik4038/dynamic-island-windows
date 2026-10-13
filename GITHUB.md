# Работа с репозиторием

Проект опубликован: **<https://github.com/Maksik4038/dynamic-island-windows>**

Репозиторий уже настроен — ветка `main` отслеживает `origin`, авторизация Git Credential
Manager сохранена. Для повседневной работы достаточно трёх команд.

---

## Как внести изменения

```powershell
cd "C:\Users\Arch\Documents\deepseek-harness\default-workspace\DynamicIsland"

git add .
git commit -m "кратко что изменилось"
git push
```

Проверить, что изменилось, до коммита:

```powershell
git status
git diff
```

---

## Если нужно выложить проект с нуля (на другой машине)

### 1. Установить Git

<https://git-scm.com/download/win> — после установки **закрыть и заново открыть**
PowerShell, иначе команда `git` не появится.

```powershell
git --version
git config --global user.name "Maksik4038"
git config --global user.email "maksikk246@gmail.com"
```

Имя и почта важны: по ним GitHub связывает коммиты с аккаунтом. Если оставить
заполнитель вроде `Ваше Имя`, автор коммита будет выглядеть как чужой человек.

### 2. Создать репозиторий на GitHub

<https://github.com/new> → имя `dynamic-island-windows`.

**Не ставить** галочки «Add a README file», «Add .gitignore», «Choose a license» —
всё это уже есть в проекте, иначе получится конфликт при первом `push`.

### 3. Отправить

```powershell
cd <папка с проектом>
git init
git add .
git status                 # exe, settings.ini и island-status.txt быть не должно
git commit -m "Dynamic Island for Windows 11: layered-window pill with live activities"
git branch -M main
git remote add origin https://github.com/Maksik4038/dynamic-island-windows.git
git push -u origin main
```

---

## Ошибки, которые уже случались

**`Repository not found` при push.** Самая частая причина — в адресе остался
заполнитель. Проверить и исправить:

```powershell
git remote -v
git remote set-url origin https://github.com/Maksik4038/dynamic-island-windows.git
```

Git **не умеет создавать репозитории на GitHub**. Если репозитория ещё нет, push
всегда вернёт `Repository not found`, даже когда адрес правильный.

**`error: remote origin already exists`.** Удалённый репозиторий уже прописан, повторно
`git remote add` не сработает. Используйте `git remote set-url` (см. выше).

**`nothing to commit, working tree clean`.** Все изменения уже закоммичены — это не
ошибка. Либо коммит уже сделан, либо файлы не попали в индекс (`git add .`).

**Автор коммита — не вы.** Поправить последний коммит, не меняя файлы:

```powershell
git config user.name "Maksik4038"
git commit --amend --reset-author --no-edit
git push --force-with-lease
```

Почта `maksikk246@gmail.com` в настройках — рабочая: она привязана к аккаунту
`Maksik4038`, поэтому GitHub связывает коммиты с профилем. Менять не нужно.

---

## Что не попадает в репозиторий

`.gitignore` исключает:

| Исключено | Почему |
|---|---|
| `DynamicIsland.exe` | собирается командой `Build.ps1` |
| `settings.ini` | личные настройки (ширина острова) |
| `island-status.txt` | рабочий файл, создаётся при запуске |
| `bin/`, `obj/`, `*.pdb` | артефакты сборки |

Снимки из `shots/` **остаются** — они используются в README как иллюстрации.

Если exe уже попал в индекс:

```powershell
git rm --cached DynamicIsland.exe
```

---

## Отдельный проект: HTML-плеер

В папке выше лежит `liquid-glass-player.html` — музыкальный плеер в стиле liquid glass.
Варианты:

- **отдельный репозиторий** — просто повторить шаги выше в его папке;
- **вместе с островом** — инициализировать репозиторий на уровень выше и разложить так:

```
dynamic-island-windows/
├── README.md          # общий, про оба проекта
├── LICENSE
├── DynamicIsland/     # остров
└── LiquidGlassPlayer/
    └── index.html
```

Чтобы GitHub Pages отдавал плеер как страницу, положите его как `index.html` в корень
репозитория и включите *Settings → Pages → Deploy from branch → main / root*.
