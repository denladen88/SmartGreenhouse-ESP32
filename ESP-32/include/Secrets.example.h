#pragma once
#include <cstdint>

// Скопіюй цей файл у Secrets.h і заповни власними даними.
// Secrets.h у .gitignore і не потрапляє в git — цей приклад лишається
// в репозиторії як документація потрібної структури.

// ---- Wi-Fi ----
constexpr const char* WIFI_SSID = "YOUR_WIFI_SSID";
constexpr const char* WIFI_PASSWORD = "YOUR_WIFI_PASSWORD";

// ---- MQTT (Mosquitto) ----
constexpr const char* MQTT_BROKER_IP = "192.168.1.100";
constexpr uint16_t MQTT_BROKER_PORT = 1883;

// Розкоментуй, якщо брокер вимагає автентифікацію:
// #define MQTT_USERNAME "user"
// #define MQTT_PASSWORD "pass"
