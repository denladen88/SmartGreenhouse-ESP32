# Деплой SmartGreenhouse (CI/CD)

Як влаштований автоматичний деплой Backend (разом із вбудованим WebApp) на домашній
сервер, що ми налаштували один раз, і як цим користуватись далі.

> Коротко: `git push` у `main` → GitHub збирає Docker-образ і кладе в GHCR →
> self-hosted runner на домашньому сервері тягне образ і перезапускає контейнер.

---

## 1. Загальна картина

```
ТВІЙ ПК                 GITHUB.COM (хмара)              lenovo-srv (вдома, за роутером)
───────                 ─────────────────              ──────────────────────────────
правиш код
git push main ────────▶ workflow "deploy" запускається
                        │
                        ├─ job backend-build (ubuntu-latest):  dotnet build -c Release
                        │        └─ швидка перевірка, що C# компілюється
                        │
                        ├─ job docker (ubuntu-latest):
                        │        docker build -f Backend/Dockerfile .   (Node→WebApp, потім .NET→API)
                        │        docker push ──▶ ghcr.io/denladen88/smartgreenhouse-backend:latest
                        │                                              :<sha-коміту>
                        │
                        └─ job deploy (self-hosted, мітка smartgreenhouse):
                                 ↓ (runner на lenovo-srv забирає job)
                                 docker login ghcr.io
                                 docker compose pull backend        ◀── тягне образ із GHCR
                                 docker compose up -d backend        ── перезапускає контейнер
                                 curl http://localhost:8080/         ── health check
                                 docker image prune -f
                                 │
                                 ▼
                        Backend живий: http://<IP lenovo-srv>:8080/
                        дані (SQLite + фото) → Docker volume smartgreenhouse_greenhouse_data
```

**Головні ідеї:**

- **Сервер нічого не компілює.** Уся збірка — у хмарі GitHub. Сервер лише завантажує
  готовий образ і перезапускає контейнер.
- **`docker build` читає `Backend/Dockerfile`. `docker compose` читає `docker-compose.yml`.**
  GitHub робить тільки `build` + `push`. `compose` виконується на сервері.
- **Self-hosted runner** потрібен, бо сервер за домашнім роутером — з інтернету до нього
  не достукатись. Runner сам відкриває вихідне зʼєднання до GitHub і забирає роботу.
- **WebApp — це не окремий процес**, а статичні файли, які збираються на етапі
  `docker build` і кладуться в `Backend/wwwroot/`; Backend їх роздає. Один процес, один порт.

---

## 2. Файли, що це забезпечують (усі в git)

| Файл | Роль |
|------|------|
| [`.github/workflows/deploy.yml`](.github/workflows/deploy.yml) | pipeline: 3 job'и — `backend-build`, `docker`, `deploy` |
| [`Backend/Dockerfile`](Backend/Dockerfile) | 3-етапна збірка: `web` (node:22 → WebApp) → `build` (dotnet sdk → publish) → `final` (dotnet aspnet, runtime) |
| [`docker-compose.yml`](docker-compose.yml) | як запускати контейнер: образ, порт `8080`, volume, `.env`, `restart: unless-stopped`. Має явний `name: smartgreenhouse` |
| [`.dockerignore`](.dockerignore) | що НЕ слати в контекст збірки (`.git`, `node_modules`, `bin/obj`, БД, інші підпроєкти) |

### Чому контекст збірки — корінь репо

`docker build` може `COPY` тільки те, що всередині **контексту**. Образу треба файли і з
`Backend/`, і з `WebApp/` — їхній спільний батько це корінь репо. Тому:

```
docker build -f Backend/Dockerfile .        # крапка = корінь репо
```

а не `docker build ./Backend`. У compose це `build: { context: ., dockerfile: Backend/Dockerfile }`.

### Чому дані в named volume, а не в `Backend/appdata/`

Runner робить `checkout` у тимчасову робочу теку, яку може перестворити. Якби БД лежала
поряд із checkout'ом (bind-mount на теку репо) — загубилась би. Named volume
`smartgreenhouse_greenhouse_data` Docker тримає окремо (`/var/lib/docker/volumes/`),
незалежно від checkout'у. `down` → новий checkout → `up` = ті самі дані.

---

## 3. Одноразове налаштування (що ми зробили)

### 3.1. Секрет на GitHub

`Repo → Settings → Secrets and variables → Actions → New repository secret`:

| Name | Value |
|------|-------|
| `GEMINI_API_KEY` | **лише саме значення ключа**, БЕЗ `Gemini__ApiKey=` спереду, без лапок, без пробілів/переносу в кінці |

> ⚠️ **Граблі, на які ми наступили:** якщо вставити в секрет цілий рядок із `.env`
> (`Gemini__ApiKey=AQ...`), крок деплою додасть свій префікс `Gemini__ApiKey=` ще раз →
> у контейнер потрапить `Gemini__ApiKey=Gemini__ApiKey=AQ...` → Google відповідає
> `API_KEY_INVALID`. У секреті — **тільки значення**.

`Api__Key` (ключ до власного API Backend, зараз `1988`) береться з закоміченого
`Backend/appsettings.json`, окремий секрет не потрібен. Захочеш ротувати — додаси секрет
`BACKEND_API_KEY` і рядок `printf 'Api__Key=%s\n' "$BACKEND_API_KEY" >> Backend/.env` у
job `deploy`, і перевведеш ключ у WebApp/MobileApp.

### 3.2. Self-hosted runner на сервері

Сервер: `lenovo-srv`, Ubuntu 26.04, користувач `den` (у групах `docker` і `sudo`),
x86_64. Docker Engine + compose-плагін уже стояли.

На цьому сервері вже був **інший** runner (для іншого репо) у `~/actions-runner/`.
Наш — в **окремій** теці, бо тека runner'а містить його креденшали й привʼязана до
одного репо; одна тека = один сервіс = один репо.

```bash
# 1. окрема тека
mkdir ~/actions-runner-greenhouse && cd ~/actions-runner-greenhouse

# 2. завантажити пакет runner'а (версію/хеш бери зі сторінки
#    Repo → Settings → Actions → Runners → New self-hosted runner → Linux/x64)
curl -o actions-runner-linux-x64-2.336.0.tar.gz -L \
  https://github.com/actions/runner/releases/download/v2.336.0/actions-runner-linux-x64-2.336.0.tar.gz
tar xzf ./actions-runner-linux-x64-2.336.0.tar.gz

# 3. зареєструвати на НАШ репо. Токен — з тієї ж сторінки runners/new (живе ~1 год).
#    Вставляти напряму, БЕЗ кутових дужок.
./config.sh --url https://github.com/denladen88/SmartGreenhouse-ESP32 \
  --token AAAAAAAAAAAAAAAAAAAAAAAAAAAAA \
  --name lenovo-srv \
  --labels smartgreenhouse \
  --unattended

# 4. поставити systemd-сервісом (піднімається після ребуту), від імені den
sudo ./svc.sh install
sudo ./svc.sh start
sudo ./svc.sh status        # active (running), у лозі: "Connected to GitHub", "Listening for Jobs"
```

Сервіс називається `actions.runner.denladen88-SmartGreenhouse-ESP32.lenovo-srv` — за
репо+іменем, тож зі старим runner'ом не конфліктує.

### 3.3. Мітка `smartgreenhouse`

Job `deploy` має `runs-on: [self-hosted, smartgreenhouse]` — GitHub віддасть job лише
runner'у, що має **обидві** мітки. `self-hosted` є в усіх автоматично; `smartgreenhouse`
додається через `--labels` при `config.sh` **або** руками:

`Repo → Settings → Actions → Runners → lenovo-srv → Labels → ＋ → smartgreenhouse`.

> ⚠️ **Граблі:** якщо job висить на `Waiting for a runner to pick up this job...` і в
> лозі `Requested labels: self-hosted, smartgreenhouse` — мітки `smartgreenhouse` на
> runner'і немає. Додай і зроби `Re-run failed jobs`.

Навіщо кастомна мітка: щоб деплой теплиці йшов саме на цей сервер, а не на інший
self-hosted runner, якщо такий зʼявиться.

---

## 4. Що відбувається на кожен `git push` у `main`

| Етап | Де | Що робить | Якщо впаде |
|------|-----|-----------|-----------|
| `backend-build` | хмарна VM | `dotnet restore` + `dotnet build -c Release` | `docker` і `deploy` не стартують; сервер недоторканий |
| `docker` | хмарна VM | `docker build` (web → build → final) + `docker push` `:latest` та `:<sha>` у GHCR; кеш шарів через `type=gha` | `deploy` пропускається; сервер недоторканий |
| `deploy` | lenovo-srv (runner) | `checkout` → генерує `Backend/.env` із секретів → `docker login ghcr.io` → `docker compose pull backend` → `docker compose up -d backend` → health check `curl localhost:8080` (20×3с) → `docker image prune -f` | health-check червоний = у лозі 80 рядків контейнера; **автовідкату немає**, крутиться те, що є |

`concurrency: group: deploy` — два push'і поспіль не деплоять одночасно, другий чекає.

При `docker compose up -d`: образ змінився → старий контейнер зупиняється й видаляється,
з нового образу створюється новий; volume `smartgreenhouse_greenhouse_data`
перечіпляється (БД і фото на місці); порт `8080:8080`; `restart: unless-stopped`.
Контейнер стартує `dotnet SmartGreenhouse.Backend.dll` → `db.Database.Migrate()` →
конект до MQTT `192.168.178.50:1883` → Kestrel на `:8080` роздає `wwwroot/` (WebApp).

---

## 5. Щоденне користування

### Задеплоїти

```bash
git push        # у гілці main
```

Далі дивись `https://github.com/denladen88/SmartGreenhouse-ESP32/actions`.
Коли `deploy` зелений — `http://<IP lenovo-srv>:8080/` віддає оновлений застосунок.

### Локальна розробка — НЕ чіпає прод

Деплой тригериться **тільки push у `main`**. Тож роби у гілці:

```bash
git switch -c feature/xyz
# ...правки, коміти...
git push -u origin feature/xyz     # нічого не деплоїться
```

Злиття в `main` = деплой.

**Режим A — швидко, без Docker (щоденний):**

```bash
# термінал 1
cd Backend && dotnet run                 # http://localhost:5080, локальний greenhouse.db

# термінал 2
cd WebApp && npm run dev                 # http://localhost:5173; у Settings вкажи Backend URL = http://localhost:5080
```

**Режим B — перевірити справжній контейнер перед злиттям:**

```bash
docker compose up -d --build             # той самий Dockerfile, локальний образ+volume, http://localhost:8080
docker compose logs -f backend
docker compose down
```

Твій ПК і сервер — окремі Docker-демони. Локальний `docker compose up` контейнери
піднімає тільки на ПК.

> ⚠️ **MQTT спільний.** `appsettings.json` вказує на `192.168.178.50:1883`. Backend,
> запущений локально в тій самій Wi-Fi, підключиться до **реального** брокера і зможе
> слати команди на **реальний ESP-32**. Щоб ізолюватись — у локальний `Backend/.env`
> додай `Mqtt__Server=localhost` (або підніми локальний Mosquitto).

Для AI Agronomist локально потрібен валідний Gemini-ключ у локальному `Backend/.env`
(секрет GitHub туди не приходить).

---

## 6. Операції на сервері

```bash
# усі команди compose працюють з будь-якої теки завдяки name: smartgreenhouse
docker compose -p smartgreenhouse ps                     # запущений? порти, health
docker compose -p smartgreenhouse logs -f backend        # живий лог (Ctrl+C вийти)
docker compose -p smartgreenhouse logs --tail=100 --since=15m backend
docker compose -p smartgreenhouse exec backend sh        # шелл усередині контейнера
docker stats smartgreenhouse-backend-1                   # CPU / памʼять наживо

# дані (SQLite + фото) у named volume
docker volume inspect smartgreenhouse_greenhouse_data
# бекап БД на хост:
docker run --rm -v smartgreenhouse_greenhouse_data:/data -v "$PWD":/backup alpine \
  cp /data/greenhouse.db /backup/greenhouse.db.bak
```

### Ручний деплой (якщо треба обійти CI)

```bash
cd ~/actions-runner-greenhouse/_work/SmartGreenhouse-ESP32/SmartGreenhouse-ESP32
# або будь-який свіжий клон репо з правильним docker-compose.yml
echo "Gemini__ApiKey=<ключ>" > Backend/.env
echo "<GHCR_TOKEN>" | docker login ghcr.io -u denladen88 --password-stdin
docker compose pull backend
docker compose up -d backend
```

---

## 7. Відкат

Автовідкату немає. Варіанти:

**А. Задеплоїти попередній образ за тегом-хешем:**

```bash
# на сервері, у теці з docker-compose.yml
docker compose -p smartgreenhouse pull backend           # переконатись, що образи є
docker pull ghcr.io/denladen88/smartgreenhouse-backend:<старий-sha>
docker tag ghcr.io/denladen88/smartgreenhouse-backend:<старий-sha> \
          ghcr.io/denladen88/smartgreenhouse-backend:latest
docker compose -p smartgreenhouse up -d backend
```

**Б. Через git (запускає нормальний pipeline):**

```bash
git revert <поганий-коміт>
git push        # backend-build → docker → deploy зі старим кодом
```

---

## 8. Траблшутінг

| Симптом | Причина | Фікс |
|---------|---------|------|
| `deploy` висить `Waiting for a runner`, у лозі `Requested labels: self-hosted, smartgreenhouse` | на runner'і немає мітки `smartgreenhouse` | додати мітку (розд. 3.3), `Re-run failed jobs` |
| runner **Offline** на сторінці Runners | сервіс не працює | `cd ~/actions-runner-greenhouse && sudo ./svc.sh start`; `journalctl -u actions.runner.denladen88-SmartGreenhouse-ESP32.lenovo-srv -n 30` |
| у лозі Gemini `API_KEY_INVALID`, а `printenv \| grep -i gemini` показує подвоєний `Gemini__ApiKey=Gemini__ApiKey=...` | у секрет вставлено цілий рядок `.env` замість значення | оновити секрет `GEMINI_API_KEY` — тільки значення; `Re-run` |
| `API_KEY_INVALID` при одинарному префіксі | ключ протермінований / не той | новий ключ https://aistudio.google.com/apikey (формат `AIza...`), оновити секрет, `Re-run` |
| Google `model not found` / 404 | `Gemini:Model` у `appsettings.json` неактуальний (`gemini-3.5-flash`) | замінити на актуальну модель, закомітити, push |
| `docker compose up` → `port is already allocated` | 8080 на сервері вже зайнято | у `docker-compose.yml` змінити ліву цифру: `ports: - "8090:8080"`, push |
| контейнер стартує, але не бачить MQTT-брокер / ESP32-CAM | bridge-мережа не пускає до LAN | у `docker-compose.yml` розкоментувати `network_mode: host`, push |
| health-check червоний, у лозі контейнера exception на старті | напр. збій міграції БД, поганий конфіг | глянути `docker compose -p smartgreenhouse logs --tail=100 backend`; за потреби відкат (розд. 7) |

---

## 9. Відтворити з нуля (чекліст)

Якщо переносиш на новий сервер або все злетіло:

1. **Сервер:** Ubuntu x86_64, встановити Docker Engine + compose-плагін, користувача
   додати в групу `docker`.
2. **GitHub secret:** `GEMINI_API_KEY` = значення ключа (розд. 3.1).
3. **Runner:** розд. 3.2 — окрема тека, `config.sh --url ... --labels smartgreenhouse`,
   `svc.sh install/start`.
4. **Мітка:** переконатись, що на runner'і є `smartgreenhouse` (розд. 3.3).
5. **Перший деплой:** `git commit --allow-empty -m "redeploy" && git push` → дивитись
   Actions → дочекатись зеленого `deploy`.
6. **Перевірка:** `http://<IP сервера>:8080/` відкриває WebApp;
   `docker compose -p smartgreenhouse logs -f backend` без критичних помилок.
7. **(За потреби)** якщо контейнер не бачить MQTT — `network_mode: host` у compose;
   якщо 8080 зайнято — змінити порт.

Файли `.github/workflows/deploy.yml`, `Backend/Dockerfile`, `docker-compose.yml`,
`.dockerignore` вже в репо — окремо створювати не треба.
