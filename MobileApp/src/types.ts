// Дзеркалить DTO з Backend/Models — System.Text.Json за замовчуванням
// серіалізує camelCase, окрім AiCommand (явний JsonPropertyName snake_case
// для сумісності з MQTT-стороною ESP32/Gemini).

export interface TelemetryRecord {
  id: string;
  timestamp: string;
  deviceId: string;
  uptimeMs: number;
  temperatureC: number | null;
  humidityPct: number | null;
  pressureHpa: number | null;
  lux: number | null;
  soilRaw: number;
  soilMoisturePct: number | null;
  soilTempC: number | null;
}

export interface AiDecisionRecord {
  id: string;
  timestamp: string;
  pumpOn: boolean;
  fanOn: boolean;
  lightBrightness: number;
  soilHeaterPower: number;
  reason: string;
  photoDescription: string;
}

export interface AiCommand {
  pump_on: boolean;
  fan_on: boolean;
  light_brightness: number;
  soil_heater_power: number;
}

export interface PlantProfile {
  id: string;
  plantName: string;
  tempMinC: number;
  tempMaxC: number;
  humidityMinPct: number;
  humidityMaxPct: number;
  soilMoistureMinPct: number;
  soilMoistureMaxPct: number;
  soilTempMinC: number;
  soilTempMaxC: number;
  dailyLightHoursTarget: number;
  growthStage: string;
  notes: string;
  lastUpdatedUtc: string;
  lastUpdateReason: string;
}

export interface Planting {
  id: string;
  plantName: string;
  soilType: string;
  plantedDateUtc: string;
  notes: string;
  createdUtc: string;
}

export interface PlantingRequest {
  plantName: string;
  soilType: string;
  plantedDateUtc: string;
  notes: string;
}
