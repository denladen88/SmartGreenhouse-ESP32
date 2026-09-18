#include "NetworkService.h"
#include <Arduino.h>
#include <WiFi.h>
#include "Config.h"
#include "Secrets.h"

namespace {
// Коди причин розриву з esp_wifi_types.h (wifi_err_reason_t) — саме вони
// відповідають на питання "чому плата пішла в офлайн": слабкий сигнал/завади
// (BEACON_TIMEOUT), роутер сам розірвав з'єднання (ASSOC_LEAVE/CONNECTION_FAIL),
// роутер перезавантажився чи змінив SSID (NO_AP_FOUND), проблема з паролем
// (AUTH_FAIL/4WAY_HANDSHAKE_TIMEOUT) тощо. Без цього логу видно лише сам факт
// розриву, а не причину.
const char* wifiDisconnectReasonStr(uint8_t reason) {
  switch (reason) {
    case WIFI_REASON_UNSPECIFIED: return "не вказано";
    case WIFI_REASON_AUTH_EXPIRE: return "час автентифікації вийшов";
    case WIFI_REASON_AUTH_LEAVE: return "точка доступу розірвала автентифікацію";
    case WIFI_REASON_ASSOC_EXPIRE: return "роутер \"забув\" пристрій (assoc expired)";
    case WIFI_REASON_ASSOC_TOOMANY: return "забагато клієнтів на точці доступу";
    case WIFI_REASON_NOT_AUTHED: return "не автентифіковано";
    case WIFI_REASON_NOT_ASSOCED: return "не асоційовано";
    case WIFI_REASON_ASSOC_LEAVE: return "плата сама розірвала з'єднання (WiFi.begin()/reconnect)";
    case WIFI_REASON_ASSOC_NOT_AUTHED: return "асоційовано без автентифікації";
    case WIFI_REASON_MIC_FAILURE: return "помилка MIC (перевір пароль/шифрування)";
    case WIFI_REASON_4WAY_HANDSHAKE_TIMEOUT: return "тайм-аут 4-way handshake (слабкий сигнал або невірний пароль)";
    case WIFI_REASON_GROUP_KEY_UPDATE_TIMEOUT: return "тайм-аут оновлення group key";
    case WIFI_REASON_BEACON_TIMEOUT: return "втрачено сигнал роутера (слабкий Wi-Fi/перешкоди)";
    case WIFI_REASON_NO_AP_FOUND: return "точку доступу не знайдено (роутер вимкнено/перезавантажується/SSID змінено)";
    case WIFI_REASON_AUTH_FAIL: return "помилка автентифікації (невірний пароль?)";
    case WIFI_REASON_ASSOC_FAIL: return "помилка асоціації з точкою доступу";
    case WIFI_REASON_HANDSHAKE_TIMEOUT: return "тайм-аут handshake";
    case WIFI_REASON_CONNECTION_FAIL: return "точка доступу розірвала з'єднання";
    case WIFI_REASON_AP_TSF_RESET: return "скидання TSF точки доступу";
    case WIFI_REASON_ROAMING: return "роумінг між точками доступу";
    default: return "невідома причина";
  }
}
}  // namespace

NetworkService::NetworkService() : _reconnectTimer(WIFI_RECONNECT_INTERVAL_MS) {}

void NetworkService::begin() {
  WiFi.mode(WIFI_STA);
  // Реєструємо ДО першого WiFi.begin(): подія розриву може прийти будь-коли
  // після нього, і хочемо ловити геть кожен розрив за весь час роботи плати,
  // а не лише ті, що трапляються після першого успішного підключення.
  WiFi.onEvent([](arduino_event_id_t event, arduino_event_info_t info) {
    Serial.printf("[WiFi] Розірвано з'єднання (код %d): %s\n",
                  info.wifi_sta_disconnected.reason,
                  wifiDisconnectReasonStr(info.wifi_sta_disconnected.reason));
  }, ARDUINO_EVENT_WIFI_STA_DISCONNECTED);
  connect();
}

void NetworkService::connect() {
  Serial.printf("[WiFi] Підключення до \"%s\"...\n", WIFI_SSID);
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  // Дефолтний modem-sleep (WIFI_PS_MIN_MODEM) приспинює радіо між пакетами:
  // вихідний трафік (MQTT, який ESP32 сам ініціює) це не заважає, але вхідні
  // з'єднання ззовні (backend'ів GET /capture) можуть губитись/таймаутити,
  // бо радіо не встигає прокинутись на вхідний SYN. Пристрій живиться від
  // мережі (не батарея), тож вимикаємо енергозбереження заради стабільності.
  WiFi.setSleep(false);
}

bool NetworkService::update() {
  bool connected = isConnected();
  // Backend звертається до /capture за жорстко заданою IP (Esp32:CameraUrl
  // у appsettings.json), а не по mDNS-імені — тож при заміні плати чи зміні
  // DHCP-оренди єдиний спосіб дізнатись актуальну адресу без доступу до
  // роутера це цей лог, друкований одноразово на кожен новий конект.
  if (connected && !_wasConnected) {
    Serial.printf("[WiFi] Підключено. IP: %s | RSSI: %d dBm\n",
                  WiFi.localIP().toString().c_str(), WiFi.RSSI());
  }
  _wasConnected = connected;

  if (!connected && _reconnectTimer.elapsed()) {
    Serial.println("[WiFi] З'єднання відсутнє, повторна спроба...");
    connect();
  }

  // Повертаємо стан, який щойно обчислили вище (WiFi.status() всередині
  // isConnected() — не найдешевший виклик): loop() бере це значення один раз
  // і передає далі (mqtt.update(), гейт публікації), замість того щоб кожен
  // споживач у тому самому проході циклу питав WiFi-драйвер про те саме.
  return connected;
}

bool NetworkService::isConnected() const {
  return WiFi.status() == WL_CONNECTED;
}
