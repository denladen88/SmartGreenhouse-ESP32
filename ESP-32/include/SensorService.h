#pragma once
#include <cstdint>
#include <Adafruit_BME280.h>
#include <BH1750.h>
#include <OneWire.h>
#include <DallasTemperature.h>
#include "NonBlockingTimer.h"

struct SensorData {
  bool climateValid = false;
  float temperatureC = 0.0f;
  float humidityPct = 0.0f;
  float pressureHpa = 0.0f;

  bool lightValid = false;
  float lux = 0.0f;

  int soilRaw = 0;
  float soilMoisturePct = 0.0f; // 0 = сухо, 100 = мокро (перевід soilRaw через SOIL_RAW_WET/DRY)
  bool soilValid = false;       // false = ймовірно від'єднаний/обірваний зонд (див. SensorService::read)

  bool soilTempValid = false;
  float soilTempC = 0.0f;
};

class SensorService {
public:
  // Ініціалізує I2C та підключені сенсори. Повертає true, якщо знайдено хоча б один.
  bool begin();

  // Викликати щоцикл loop(): роликова вибірка ADC ґрунту (щоб read() не блокував
  // loop() через delay()) і періодичне переперевіряння сенсорів, які відпали від
  // шини вже після старту.
  void update();

  SensorData read();

private:
  static constexpr int kSoilSamples = 15;

  void initBme();
  void initBh1750();
  void initSoilTemp();

  Adafruit_BME280 _bme;
  BH1750 _lightMeter;
  OneWire _oneWire;
  DallasTemperature _dallasTemp{&_oneWire};

  bool _hasBme = false;
  bool _hasBh1750 = false;
  bool _hasSoilTemp = false;

  // Скільки поспіль неправдоподібних/неуспішних читань підряд, перш ніж
  // вважати сенсор відпалим від шини й скинути has*-прапорець (щоб
  // update()'s 5-хв reprobe його підхопив) — один випадковий шумний I2C-кадр
  // не повинен видавати справний сенсор за відсутній.
  static constexpr uint8_t kMaxConsecutiveFailures = 3;
  uint8_t _bmeFailCount = 0;
  uint8_t _bh1750FailCount = 0;
  uint8_t _soilTempFailCount = 0;

  bool _soilTempPending = false; // чи очікує getTempCByIndex() замовлення з минулого read()

  int _soilRing[kSoilSamples] = {0};
  uint8_t _soilRingIdx = 0;
  bool _soilRingFull = false;
  NonBlockingTimer _soilSampleTimer{20UL};      // 1 відлік / 20 мс → ~300 мс вікна на 15 зразків
  NonBlockingTimer _reprobeTimer{300000UL};     // раз на 5 хв шукаємо відсутні сенсори
};
