#include "NetworkService.h"
#include <Arduino.h>
#include <WiFi.h>
#include "Config.h"
#include "Secrets.h"

NetworkService::NetworkService() : _reconnectTimer(WIFI_RECONNECT_INTERVAL_MS) {}

void NetworkService::begin() {
  WiFi.mode(WIFI_STA);
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

void NetworkService::update() {
  if (WiFi.status() != WL_CONNECTED && _reconnectTimer.elapsed()) {
    Serial.println("[WiFi] З'єднання відсутнє, повторна спроба...");
    connect();
  }
}

bool NetworkService::isConnected() const {
  return WiFi.status() == WL_CONNECTED;
}
