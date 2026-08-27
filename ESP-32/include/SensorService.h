#pragma once
#include <Adafruit_BME280.h>
#include <BH1750.h>
#include <OneWire.h>
#include <DallasTemperature.h>

struct SensorData {
  bool climateValid = false;
  float temperatureC = 0.0f;
  float humidityPct = 0.0f;
  float pressureHpa = 0.0f;

  bool lightValid = false;
  float lux = 0.0f;

  int soilRaw = 0;
  float soilMoisturePct = 0.0f; // 0 = сухо, 100 = мокро (перевід soilRaw через SOIL_RAW_WET/DRY)

  bool soilTempValid = false;
  float soilTempC = 0.0f;
};

class SensorService {
public:
  // Ініціалізує I2C та підключені сенсори. Повертає true, якщо знайдено хоча б один.
  bool begin();

  SensorData read();

private:
  Adafruit_BME280 _bme;
  BH1750 _lightMeter;
  OneWire _oneWire;
  DallasTemperature _dallasTemp{&_oneWire};
  bool _hasBme = false;
  bool _hasBh1750 = false;
  bool _hasSoilTemp = false;
};
