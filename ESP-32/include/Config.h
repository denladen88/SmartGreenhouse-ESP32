#pragma once

// ---- I2C (шина сенсорів, Wire/I2C0 — див. SensorService::begin()) ----
#define I2C_SDA_PIN 1
#define I2C_SCL_PIN 2
constexpr unsigned long I2C_FREQ_HZ = 100000;

constexpr uint8_t BME280_ADDR_PRIMARY = 0x76;
constexpr uint8_t BME280_ADDR_SECONDARY = 0x77;
constexpr uint8_t BH1750_ADDR = 0x23;

// ---- Аналоговий вхід ----
// GPIO3, не GPIO14: GPIO14 сидить на ADC2, який на ESP32-S3 конфліктує з
// активним Wi-Fi-радіо (підтверджено на практиці — щойно Wi-Fi реально
// підключився й почав публікувати телеметрію, показники стрибали хаотично
// від ~0 до ~4095 щоцикл). GPIO3 — останній вільний ADC1-пін (GPIO1-10),
// решта зайняті I2C-шиною сенсорів і камерою; він є strapping-піном для
// вибору джерела JTAG на ESP32-S3, але це стосується лише самого reset,
// як звичайний аналоговий вхід після завантаження працює нормально.
constexpr int SOIL_ADC_PIN = 3;
constexpr int ADC_RESOLUTION_BITS = 12;

// Калібрування ґрунтового датчика (резистивний AZDelivery, з медіанним
// фільтром 15 зразків). SOIL_RAW_DRY=4095 стабільний і незмінний — на
// розімкненому колі (повітря) стан контактної поверхні зонда не має
// значення, опір і так "нескінченний". SOIL_RAW_WET перекалібровано:
// початково було ~0 у чистій воді, але після кількох цілодобових циклів
// тестування під постійною 3.3V контакти зонда скорродували (глибша
// корозія металу, не просто поверхнева плівка — чистка наждаком/спиртом
// вже не повертає до ~0), тож поточний стабільний еталон у воді — ~1270.
// Якщо калібрування знову "попливе" з часом — це та сама корозія,
// прогресуюча далі; довгострокове вирішення — не тримати зонд під
// постійною напругою (вмикати живлення лише на час виміру).
constexpr int SOIL_RAW_WET = 1270;
constexpr int SOIL_RAW_DRY = 4095;

// ---- Актуатори ----
constexpr int PUMP_RELAY_PIN = 47;
constexpr int FAN_PIN = 21;

// Grow light: адресована 24V стрічка, RGB+TW (5 каналів на піксель:
// R,G,B,Cold White,Warm White). Adafruit_NeoPixel не підтримує 5-канальні
// пікселі напряму — ActuatorService обходить це через сирий буфер
// (див. коментар у ActuatorService.cpp).
#define LIGHT_PIN 45
#define NUM_LEDS 30
constexpr float LIGHT_THRESHOLD_LOW = 300.0f;

// Захисний ліміт: помпа автоматично вимикається, якщо працює довше цього
// часу (запобіжник від "залипання" реле/зависання команди).
constexpr unsigned long PUMP_MAX_RUNTIME_MS = 5000;

// Той самий захист для вентилятора: якщо AI-бекенд "замовк" (мережа впала,
// цикл завис тощо), вентилятор не повинен дути нескінченно — команда setFan()
// не повторюється, доки не прийде наступне MQTT-рішення (AI-цикл раз/год),
// тож без цього ліміту вентилятор міг би застрягти увімкненим на дні.
constexpr unsigned long FAN_MAX_RUNTIME_MS = 600000;

// ---- Мережа / телеметрія ----
constexpr const char* DEVICE_ID = "smartplant-s3-01";
constexpr const char* MQTT_TELEMETRY_TOPIC = "smartplant/telemetry";
constexpr const char* MQTT_STATUS_TOPIC = "smartplant/status";
constexpr const char* MQTT_COMMANDS_TOPIC = "smartplant/commands";

// ---- Інтервали циклу (неблокуючі, на базі millis()) ----
constexpr unsigned long SENSOR_READ_INTERVAL_MS = 600000;
constexpr unsigned long CAMERA_CAPTURE_INTERVAL_MS = 30000;
constexpr unsigned long LIGHT_CHECK_INTERVAL_MS = 1000;
constexpr unsigned long WIFI_RECONNECT_INTERVAL_MS = 10000;
constexpr unsigned long MQTT_RECONNECT_INTERVAL_MS = 5000;
