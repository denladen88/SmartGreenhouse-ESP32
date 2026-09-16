#include "SensorService.h"
#include <Arduino.h>
#include <Wire.h>
#include <cstring>
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

  initBme();
  initBh1750();
  initSoilTemp();

  return _hasBme || _hasBh1750 || _hasSoilTemp;
}

void SensorService::initBme() {
  _hasBme = _bme.begin(BME280_ADDR_PRIMARY, &Wire) || _bme.begin(BME280_ADDR_SECONDARY, &Wire);
  _bmeFailCount = 0;
  Serial.println(_hasBme ? "[BME280] Клімат-сенсор готовий."
                         : "[BME280] Помилка: BME280 не знайдено!");
}

void SensorService::initBh1750() {
  _hasBh1750 = _lightMeter.begin(BH1750::CONTINUOUS_HIGH_RES_MODE, BH1750_ADDR, &Wire);
  _bh1750FailCount = 0;
  Serial.println(_hasBh1750 ? "[BH1750] Люксметр готовий."
                            : "[BH1750] Помилка: BH1750 не знайдено!");
}

void SensorService::initSoilTemp() {
  // OneWire-пін МАЄ бути <= 33: paulstoffregen/OneWire 2.3.8 у
  // util/OneWire_direct_gpio.h (directModeOutput) для пінів >33 мовчки не
  // перемикає лінію у вихід — reset-імпульс не формується, getDeviceCount()
  // повертає 0. Див. коментар біля SOIL_TEMP_ONEWIRE_PIN у Config.h.
  _oneWire.begin(SOIL_TEMP_ONEWIRE_PIN);
  _dallasTemp.begin();
  // 9-біт (крок 0.5°C) замість дефолтних 12-біт: конверсія ~94мс замість ~750мс.
  _dallasTemp.setResolution(9);
  // Неблокуюче замовлення: requestTemperatures() більше не крутить busy-wait
  // ~94-750мс усередині loop(). read() забирає результат наступного циклу.
  _dallasTemp.setWaitForConversion(false);
  _soilTempPending = false;
  _hasSoilTemp = _dallasTemp.getDeviceCount() > 0;
  _soilTempFailCount = 0;
  Serial.println(_hasSoilTemp ? "[DS18B20] Ґрунтовий термодатчик готовий."
                              : "[DS18B20] Помилка: датчик не знайдено на OneWire-шині!");
}

void SensorService::update() {
  // Роликова вибірка ADC ґрунту: 1 відлік на кожні 20 мс у кільцевий буфер.
  // read() бере медіану наявних зразків без жодного delay() — на відміну від
  // попередньої версії, що блокувала loop() на 15×delay(2)=30мс щочитання.
  if (_soilSampleTimer.elapsed()) {
    _soilRing[_soilRingIdx] = analogRead(SOIL_ADC_PIN);
    if (++_soilRingIdx >= kSoilSamples) {
      _soilRingIdx = 0;
      _soilRingFull = true;
    }
  }

  // Сенсор, що відпав від шини після старту (розхитаний конектор, просадка
  // живлення), інакше лишався б «мертвим» до перезавантаження. Пробуємо знайти
  // відсутні раз на 5 хв. Якщо всі на місці — таймер навіть не чіпаємо
  // (короткий &&), тож перша ж втрата зв'язку переперевіряється одразу.
  if ((!_hasBme || !_hasBh1750 || !_hasSoilTemp) && _reprobeTimer.elapsed()) {
    if (!_hasBme) initBme();
    if (!_hasBh1750) initBh1750();
    if (!_hasSoilTemp) initSoilTemp();
  }
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
      _bmeFailCount = 0;
    } else {
      Serial.printf("[BME280] Відкинуто неправдоподібний вимір (%.2f°C, %.2f%%, %.2fhPa) — ймовірно, збій I2C.\n",
                    temp, hum, pres);
      // N поспіль неправдоподібних вимірів — це вже не шум, а сенсор, що
      // відпав від шини (розхитаний конектор, просадка живлення). Скидаємо
      // has-прапорець, щоб update()'s reprobe (кожні 5 хв) підхопив його
      // повернення — інакше читання лишались би "мертвими" до перезавантаження.
      if (++_bmeFailCount >= kMaxConsecutiveFailures) {
        _hasBme = false;
        Serial.println("[BME280] Забагато поспіль неправдоподібних вимірів — вважаємо відключеним, чекаємо reprobe.");
      }
    }
  }

  if (_hasBh1750) {
    float lux = _lightMeter.readLightLevel();
    // Верхня межа BH1750 у режимі High-Res — 54612,5 lx; усе, що впритул до
    // цього значення чи вище, майже напевно теж збій читання, а не реальне світло.
    if (lux >= 0.0f && lux < 54612.0f) {
      data.lightValid = true;
      data.lux = lux;
      _bh1750FailCount = 0;
    } else {
      Serial.printf("[BH1750] Відкинуто неправдоподібний вимір (%.1f lx) — ймовірно, збій I2C.\n", lux);
      if (++_bh1750FailCount >= kMaxConsecutiveFailures) {
        _hasBh1750 = false;
        Serial.println("[BH1750] Забагато поспіль неправдоподібних вимірів — вважаємо відключеним, чекаємо reprobe.");
      }
    }
  }

  // --- Ґрунтова вологість: медіана з роликового буфера (update(), 15×20мс).
  // Стійка до «брязкоту» резистивного вузла біля розриву кола: поки більшість
  // зразків в одному стані, викиди меншості на медіану не впливають. ---
  int sorted[kSoilSamples];
  int count = _soilRingFull ? kSoilSamples : (int)_soilRingIdx;
  if (count < 1) {
    // update() ще жодного разу не відпрацював (перші мс після старту) — разовий відлік.
    sorted[0] = analogRead(SOIL_ADC_PIN);
    count = 1;
  } else {
    memcpy(sorted, _soilRing, count * sizeof(sorted[0]));
  }
  for (int i = 1; i < count; i++) {
    int key = sorted[i];
    int j = i - 1;
    while (j >= 0 && sorted[j] > key) {
      sorted[j + 1] = sorted[j];
      j--;
    }
    sorted[j + 1] = key;
  }
  data.soilRaw = sorted[count / 2];

  // Від'єднаний резистивний зонд: пін «плаває» біля верхньої межі ADC із великим
  // розкидом між зразками. Зонд у ґрунті (навіть сухому) дає стабільну медіану з
  // малим розкидом → сухий ґрунт (raw≈4095, розкид малий) лишається valid, а
  // обрив (raw біля межі + розкид великий) позначається невалідним. Остаточне
  // залізне рішення — pull-down 10 кОм на GPIO3: тоді обрив кола = ~0.
  int spread = sorted[count - 1] - sorted[0];
  data.soilValid = !(data.soilRaw >= 3900 && spread > 400);

  // Переведення сирого ADC у відсоток вологості за підтвердженими еталонами.
  // Формула загальна (не припускає WET==0), щоб калібрування можна було
  // змінити пізніше без переписування логіки.
  float pct = 100.0f * (float)(SOIL_RAW_DRY - data.soilRaw) / (float)(SOIL_RAW_DRY - SOIL_RAW_WET);
  data.soilMoisturePct = constrain(pct, 0.0f, 100.0f);

  // --- DS18B20: неблокуюче читання. Значення, замовлене в МИНУЛОМУ виклику
  // read() (setWaitForConversion(false)), давно готове — забираємо його й одразу
  // замовляємо наступне. Перший виклик після старту: soilTempValid лишається
  // false (замовлення ще не робилось) — для теплової маси ґрунту прийнятно. ---
  if (_hasSoilTemp) {
    if (_soilTempPending) {
      float t = _dallasTemp.getTempCByIndex(0);
      if (t != DEVICE_DISCONNECTED_C) {
        data.soilTempValid = true;
        data.soilTempC = t;
        _soilTempFailCount = 0;
      } else {
        Serial.println("[DS18B20] Датчик не відповів під час читання.");
        if (++_soilTempFailCount >= kMaxConsecutiveFailures) {
          _hasSoilTemp = false;
          Serial.println("[DS18B20] Забагато поспіль невдалих читань — вважаємо відключеним, чекаємо reprobe.");
        }
      }
    }
    _dallasTemp.requestTemperatures(); // миттєво (setWaitForConversion(false))
    _soilTempPending = true;
  }

  return data;
}
