#include "SensorService.h"
#include <Arduino.h>
#include <Wire.h>
#include "Config.h"

bool SensorService::begin() {
  // Звичайний Wire (I2C0), НЕ Wire1: емпірично підтверджено (лог з плати),
  // що SCCB-драйвер камери (esp32-camera) сам займає I2C1. Якщо сенсори теж
  // підуть на I2C1 (Wire1), у камери падає esp_camera_init() з
  // "i2c driver install error" / "sccb init err". Тримай сенсори на Wire
  // (I2C0), а камеру не чіпай — вона сама ініціалізує свою SCCB-шину.
  // Це єдине місце виклику begin() для шини сенсорів.
  Wire.begin(I2C_SDA_PIN, I2C_SCL_PIN, I2C_FREQ_HZ);
  analogReadResolution(ADC_RESOLUTION_BITS);

  _hasBme = _bme.begin(BME280_ADDR_PRIMARY, &Wire) || _bme.begin(BME280_ADDR_SECONDARY, &Wire);
  if (_hasBme) {
    Serial.println("[BME280] Клімат-сенсор готовий.");
  } else {
    Serial.println("[BME280] Помилка: BME280 не знайдено!");
  }

  _hasBh1750 = _lightMeter.begin(BH1750::CONTINUOUS_HIGH_RES_MODE, BH1750_ADDR, &Wire);
  if (_hasBh1750) {
    Serial.println("[BH1750] Люксметр готовий.");
  } else {
    Serial.println("[BH1750] Помилка: BH1750 не знайдено!");
  }

  // OneWire-пін МАЄ бути <= 33: paulstoffregen/OneWire 2.3.8 у
  // util/OneWire_direct_gpio.h (directModeOutput) для пінів >33 мовчки не
  // перемикає лінію у вихід — reset-імпульс не формується, getDeviceCount()
  // повертає 0. Див. коментар біля SOIL_TEMP_ONEWIRE_PIN у Config.h.
  _oneWire.begin(SOIL_TEMP_ONEWIRE_PIN);
  _dallasTemp.begin();
  // 9-біт (крок 0.5°C) замість дефолтних 12-біт: конверсія займає ~94мс
  // замість ~750мс — read() блокує loop() лише на цей час раз на
  // SENSOR_READ_INTERVAL_MS, точність 0.5°C для ґрунту цілком достатня.
  _dallasTemp.setResolution(9);
  _hasSoilTemp = _dallasTemp.getDeviceCount() > 0;
  if (_hasSoilTemp) {
    Serial.println("[DS18B20] Ґрунтовий термодатчик готовий.");
  } else {
    Serial.println("[DS18B20] Помилка: датчик не знайдено на OneWire-шині!");
  }

  return _hasBme || _hasBh1750 || _hasSoilTemp;
}

SensorData SensorService::read() {
  SensorData data;

  if (_hasBme) {
    float temp = _bme.readTemperature();
    float hum = _bme.readHumidity();
    float pres = _bme.readPressure() / 100.0f;

    // Діапазон роботи BME280 за даташитом: -40..+85 °C, 0..100 % RH,
    // 300..1100 hPa. Усе поза цим — гарантовано збій I2C-читання
    // (шумна/нестабільна лінія), а не реальний вимір.
    bool plausible = temp > -40.0f && temp < 85.0f &&
                      hum >= 0.0f && hum <= 100.0f &&
                      pres > 300.0f && pres < 1100.0f;

    if (plausible) {
      data.climateValid = true;
      data.temperatureC = temp;
      data.humidityPct = hum;
      data.pressureHpa = pres;
    } else {
      Serial.printf("[BME280] Відкинуто неправдоподібний вимір (%.2f°C, %.2f%%, %.2fhPa) — ймовірно, збій I2C.\n",
                    temp, hum, pres);
    }
  }

  if (_hasBh1750) {
    float lux = _lightMeter.readLightLevel();
    // Верхня межа BH1750 у режимі High-Res — 54612,5 lx (одиниці виміру
    // модуля обмежені 16-бітним регістром); усе, що впритул до цього
    // значення чи вище, майже напевно теж збій читання, а не реальне світло.
    if (lux >= 0.0f && lux < 54612.0f) {
      data.lightValid = true;
      data.lux = lux;
    } else {
      Serial.printf("[BH1750] Відкинуто неправдоподібний вимір (%.1f lx) — ймовірно, збій I2C.\n", lux);
    }
  }

  // Медіана з розтягнутої в часі вибірки замість простого середнього.
  // На межі повної сухості (ґрунт/повітря близькі до розриву кола) вузол
  // непередбачувано "перемикається" між крайніми станами на масштабі
  // десятків мс — просте середнє в такому разі просто повертає крайній
  // стан, що трапився в вибірці, як є. Медіана стійкіша до цього: поки
  // більшість зразків лежить в одному стані, викиди меншості на неї не
  // впливають. Розтягуємо вибірку на ~40мс (не миттєво поспіль), щоб
  // частіше захопити обидві фази нестабільного сигналу в одному вимірі.
  constexpr int SOIL_SAMPLES = 15;
  int soilSamples[SOIL_SAMPLES];
  for (int i = 0; i < SOIL_SAMPLES; i++) {
    soilSamples[i] = analogRead(SOIL_ADC_PIN);
    delay(2);
  }
  // Сортування вставками — вибірка мала (15 елементів), продуктивність не критична.
  for (int i = 1; i < SOIL_SAMPLES; i++) {
    int key = soilSamples[i];
    int j = i - 1;
    while (j >= 0 && soilSamples[j] > key) {
      soilSamples[j + 1] = soilSamples[j];
      j--;
    }
    soilSamples[j + 1] = key;
  }
  data.soilRaw = soilSamples[SOIL_SAMPLES / 2];

  // Переведення сирого ADC у відсоток вологості за підтвердженими еталонами
  // (SOIL_RAW_WET у воді, SOIL_RAW_DRY на повітрі/сухому ґрунті). Формула
  // загальна (не припускає, що WET дорівнює 0), щоб калібрування можна було
  // змінити пізніше без переписування логіки.
  float pct = 100.0f * (float)(SOIL_RAW_DRY - data.soilRaw) / (float)(SOIL_RAW_DRY - SOIL_RAW_WET);
  data.soilMoisturePct = constrain(pct, 0.0f, 100.0f);

  if (_hasSoilTemp) {
    _dallasTemp.requestTemperatures();
    float t = _dallasTemp.getTempCByIndex(0);
    if (t != DEVICE_DISCONNECTED_C) {
      data.soilTempValid = true;
      data.soilTempC = t;
    } else {
      Serial.println("[DS18B20] Датчик не відповів під час читання.");
    }
  }

  return data;
}
