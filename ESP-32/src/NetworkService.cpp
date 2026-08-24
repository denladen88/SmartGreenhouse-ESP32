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
