#pragma once
#include "NonBlockingTimer.h"

// Керує Wi-Fi з'єднанням: підключається неблокуюче та автоматично
// повторює спробу через WIFI_RECONNECT_INTERVAL_MS у разі втрати зв'язку.
class NetworkService {
public:
  NetworkService();

  void begin();
  void update(); // викликати кожен цикл loop()
  bool isConnected() const;

private:
  void connect();

  NonBlockingTimer _reconnectTimer;
};
