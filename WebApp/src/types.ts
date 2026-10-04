// Дзеркалить DTO з Backend/Models — System.Text.Json за замовчуванням
// серіалізує camelCase, окрім AiCommand (явний JsonPropertyName snake_case
// для сумісності з MQTT-стороною ESP32/Gemini). Той самий контракт, що й у
// ../MobileApp/src/types.ts.

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
  exhaustFanOn: boolean;
  lightBrightness: number;
  soilHeaterPower: number;
  airHeaterPower: number;
  source: 'LocalController' | 'ManualOverride' | 'StressTest' | string;
  pumpReason: string;
  fanReason: string;
  exhaustFanReason: string;
  lightReason: string;
  soilHeaterReason: string;
  airHeaterReason: string;
  reason: string;
  photoDescription: string;
}

export interface WateringTodaySummary {
  localDate: string;
  count: number;
  lastWateringUtc: string | null;
}

export interface AiCommand {
  pump_on: boolean;
  fan_on: boolean;
  exhaust_fan_on: boolean;
  light_brightness: number;
  soil_heater_power: number;
  air_heater_power: number;
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
  lastAiPrompt: string;
  lastAiResponse: string;
  lastAiHadPhoto: boolean;
  lastAiReviewedUtc: string | null;
  lastUpdatedUtc: string;
  lastUpdateReason: string;
}

export interface AutomationRule {
  id: string;
  actuator: string;
  purpose: string;
  trigger: string;
  stop: string;
  safety: string;
}

export interface AutomationOverview {
  controller: string;
  evaluation: string;
  rules: AutomationRule[];
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
