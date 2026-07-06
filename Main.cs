/*
  Warren Occult Museum Flashlight Firmware
  Blended version:
  - Original Evan BLE beacon scanning + NeoPixel behaviors
  - Added Museum BLE identity/session API for guest media/session tracking

  IMPORTANT:
  This firmware attempts to scan for BLE room beacons while also advertising
  a GATT service for session/status communication. This must be field-tested
  on the exact ESP32 board. If scan + advertise is unstable, use a separate
  room receiver to scan flashlight advertisements, or split duties across devices.

  Update per flashlight:
  - FLASHLIGHT_ID
*/

#include <Arduino.h>
#include <BLEDevice.h>
#include <BLEScan.h>
#include <BLEAdvertisedDevice.h>
#include <BLEServer.h>
#include <BLEUtils.h>
#include <BLE2902.h>
#include <Adafruit_NeoPixel.h>

// ==================================================
// HARDWARE CONFIG
// ==================================================

#define LED_PIN 4
#define LED_COUNT 12

Adafruit_NeoPixel strip(LED_COUNT, LED_PIN, NEO_GRB + NEO_KHZ800);

// ==================================================
// BLE SCAN / ROOM BEACON CONFIG
// ==================================================

#define SCAN_TIME 2
#define RSSI_THRESHOLD -80

// ==================================================
// LIGHT CONFIG
// ==================================================

#define MAX_BRIGHTNESS 255 // 100%

#define WARM_R 255
#define WARM_G 147
#define WARM_B 41

// ==================================================
// MUSEUM SESSION API CONFIG
// ==================================================

// Change this per physical flashlight.
#define FLASHLIGHT_ID "FL-017"
#define FIRMWARE_VERSION "1.1.0"

// Custom Warren Museum BLE UUIDs
#define WOM_SERVICE_UUID     "7c4f0001-9b8a-4d91-9e3f-000000000001"
#define DEVICE_ID_UUID       "7c4f0002-9b8a-4d91-9e3f-000000000002"
#define SESSION_ID_UUID      "7c4f0003-9b8a-4d91-9e3f-000000000003"
#define STATUS_UUID          "7c4f0004-9b8a-4d91-9e3f-000000000004"
#define COMMAND_UUID         "7c4f0005-9b8a-4d91-9e3f-000000000005"

BLECharacteristic *sessionChar = nullptr;
BLECharacteristic *statusChar = nullptr;
BLECharacteristic *commandChar = nullptr;

String sessionId = "UNASSIGNED";

// ==================================================
// STATE
// ==================================================

String detectedName = "";
String lastDetected = "";

// Flicker state
bool flickerActive = false;
unsigned long lastFlickerTime = 0;
int flickerBrightness = MAX_BRIGHTNESS;
bool flickerDimming = true;

// Rainbow state
bool rainbowActive = false;
unsigned long lastRainbowTime = 0;
uint16_t rainbowOffset = 0;

#define RAINBOW_SPEED 5
#define RAINBOW_INTERVAL 20

// ==================================================
// FORWARD DECLARATIONS
// ==================================================

void updateMuseumStatus();
void startBLEScan();

// ==================================================
// LED HELPERS
// ==================================================

void setAll(uint8_t r, uint8_t g, uint8_t b) {
  for (int i = 0; i < LED_COUNT; i++) {
    strip.setPixelColor(i, r, g, b);
  }
  strip.show();
}

void warmWhite() {
  strip.setBrightness(MAX_BRIGHTNESS);
  setAll(WARM_R, WARM_G, WARM_B);
}

// Color wheel for rainbow
uint32_t colorWheel(uint8_t pos) {
  pos = 255 - pos;

  if (pos < 85) {
    return strip.Color(255 - pos * 3, 0, pos * 3);
  } else if (pos < 170) {
    pos -= 85;
    return strip.Color(0, pos * 3, 255 - pos * 3);
  } else {
    pos -= 170;
    return strip.Color(pos * 3, 255 - pos * 3, 0);
  }
}

// Non-blocking rainbow chase
void updateRainbow() {
  if (millis() - lastRainbowTime >= RAINBOW_INTERVAL) {
    lastRainbowTime = millis();

    for (int i = 0; i < LED_COUNT; i++) {
      strip.setPixelColor(i, colorWheel(((i * 256 / LED_COUNT) + rainbowOffset) & 255));
    }

    strip.show();

    rainbowOffset += RAINBOW_SPEED;
    if (rainbowOffset >= 256) rainbowOffset = 0;
  }
}

// Non-blocking flicker
void updateFlicker() {
  if (millis() - lastFlickerTime >= random(20, 120)) {
    lastFlickerTime = millis();

    int change = random(5, 40);

    if (flickerDimming) {
      flickerBrightness -= change;
    } else {
      flickerBrightness += change;
    }

    int minBright = (lastDetected == "Red Flicker") ? 25 :
                    (lastDetected == "Dying Flicker") ? 0 :
                    (lastDetected == "Red Dying Flicker") ? 0 : 20;

    int maxBright = (lastDetected == "Red Flicker") ? 230 :
                    (lastDetected == "Dying Flicker") ? 128 :
                    (lastDetected == "Red Dying Flicker") ? 128 : MAX_BRIGHTNESS;

    if (flickerBrightness <= minBright) {
      flickerBrightness = minBright;
      flickerDimming = false;
    } else if (flickerBrightness >= maxBright) {
      flickerBrightness = maxBright;
      flickerDimming = true;
    }

    if (random(10) > 7) {
      flickerDimming = !flickerDimming;
    }

    strip.setBrightness(flickerBrightness);

    if (lastDetected == "Red Flicker" || lastDetected == "Red Dying Flicker") {
      setAll(255, 0, 0);
    } else {
      setAll(WARM_R, WARM_G, WARM_B);
    }
  }
}

// ==================================================
// MUSEUM API HELPERS
// ==================================================

int getBatteryPercent() {
  // TODO: Replace with real battery voltage reading later.
  return 100;
}

void updateMuseumStatus() {
  String statusJson = "{";
  statusJson += "\"flashlight_id\":\"" + String(FLASHLIGHT_ID) + "\",";
  statusJson += "\"session_id\":\"" + sessionId + "\",";
  statusJson += "\"last_detected\":\"" + lastDetected + "\",";
  statusJson += "\"detected_name\":\"" + detectedName + "\",";
  statusJson += "\"flicker_active\":" + String(flickerActive ? "true" : "false") + ",";
  statusJson += "\"rainbow_active\":" + String(rainbowActive ? "true" : "false") + ",";
  statusJson += "\"battery\":" + String(getBatteryPercent()) + ",";
  statusJson += "\"firmware\":\"" + String(FIRMWARE_VERSION) + "\"";
  statusJson += "}";

  if (statusChar) {
    statusChar->setValue(statusJson.c_str());
    statusChar->notify();
  }

  if (sessionChar) {
    sessionChar->setValue(sessionId.c_str());
  }
}

class SessionCallbacks : public BLECharacteristicCallbacks {
  void onWrite(BLECharacteristic *pCharacteristic) override {
    String value = pCharacteristic->getValue().c_str();

    if (value.length() > 0) {
      sessionId = value;
      Serial.print("Session assigned: ");
      Serial.println(sessionId);
      updateMuseumStatus();
    }
  }
};

class MuseumCommandCallbacks : public BLECharacteristicCallbacks {
  void onWrite(BLECharacteristic *pCharacteristic) override {
    String command = pCharacteristic->getValue().c_str();

    Serial.print("Museum command received: ");
    Serial.println(command);

    if (command == "CLEAR_SESSION") {
      sessionId = "UNASSIGNED";
      if (sessionChar) sessionChar->setValue(sessionId.c_str());
    } else if (command == "PING") {
      // No behavior change. Status update confirms device is alive.
    } else if (command == "FORCE_WARM") {
      flickerActive = false;
      rainbowActive = false;
      lastDetected = "";
      warmWhite();
    } else if (command == "FORCE_OFF") {
      flickerActive = false;
      rainbowActive = false;
      strip.setBrightness(0);
      setAll(0, 0, 0);
    }

    updateMuseumStatus();
  }
};

void setupMuseumBLEIdentity() {
  BLEServer *server = BLEDevice::createServer();
  BLEService *service = server->createService(WOM_SERVICE_UUID);

  BLECharacteristic *deviceIdChar = service->createCharacteristic(
    DEVICE_ID_UUID,
    BLECharacteristic::PROPERTY_READ
  );
  deviceIdChar->setValue(FLASHLIGHT_ID);

  sessionChar = service->createCharacteristic(
    SESSION_ID_UUID,
    BLECharacteristic::PROPERTY_READ |
    BLECharacteristic::PROPERTY_WRITE
  );
  sessionChar->setCallbacks(new SessionCallbacks());
  sessionChar->setValue(sessionId.c_str());

  statusChar = service->createCharacteristic(
    STATUS_UUID,
    BLECharacteristic::PROPERTY_READ |
    BLECharacteristic::PROPERTY_NOTIFY
  );
  statusChar->addDescriptor(new BLE2902());

  commandChar = service->createCharacteristic(
    COMMAND_UUID,
    BLECharacteristic::PROPERTY_WRITE
  );
  commandChar->setCallbacks(new MuseumCommandCallbacks());

  service->start();

  BLEAdvertising *advertising = BLEDevice::getAdvertising();
  advertising->addServiceUUID(WOM_SERVICE_UUID);
  advertising->setScanResponse(true);
  advertising->start();

  updateMuseumStatus();

  Serial.println("Museum BLE identity service started.");
}

// ==================================================
// BLE ROOM BEACON SCANNING
// ==================================================

class ScanCallback : public BLEAdvertisedDeviceCallbacks {
  void onResult(BLEAdvertisedDevice device) {
    if (device.getRSSI() < RSSI_THRESHOLD) return;
    if (!device.haveName()) return;

    String name = device.getName().c_str();

    if (name == "Red" || name == "Blacklight" ||
        name == "Flicker" || name == "Green" ||
        name == "Off" || name == "Red Flicker" ||
        name == "White" || name == "Rainbow" ||
        name == "Dying Flicker" || name == "Red Dying Flicker") {
      detectedName = name;
    }
  }
};

void scanComplete(BLEScanResults results) {
  Serial.print("Scan done. Detected: ");
  Serial.println(detectedName);

  if (detectedName != lastDetected) {
    lastDetected = detectedName;
    flickerActive = false;
    rainbowActive = false;

    if (detectedName == "Red") {
      Serial.println("Red beacon: solid red");
      strip.setBrightness(MAX_BRIGHTNESS);
      setAll(255, 0, 0);
    } else if (detectedName == "Blacklight") {
      Serial.println("Blacklight beacon: UV purple");
      strip.setBrightness(MAX_BRIGHTNESS);
      setAll(148, 0, 211);
    } else if (detectedName == "Flicker") {
      Serial.println("Flicker beacon: dying warm white bulb");
      flickerActive = true;
      flickerBrightness = MAX_BRIGHTNESS;
      flickerDimming = true;
    } else if (detectedName == "Green") {
      Serial.println("Green beacon: solid green");
      strip.setBrightness(MAX_BRIGHTNESS);
      setAll(0, 255, 0);
    } else if (detectedName == "Off") {
      Serial.println("Off beacon: LEDs off");
      strip.setBrightness(0);
      setAll(0, 0, 0);
    } else if (detectedName == "Red Flicker") {
      Serial.println("Red Flicker beacon: dying red bulb");
      flickerActive = true;
      flickerBrightness = 230;
      flickerDimming = true;
      setAll(255, 0, 0);
    } else if (detectedName == "White") {
      Serial.println("White beacon: solid white");
      strip.setBrightness(MAX_BRIGHTNESS);
      setAll(255, 255, 255);
    } else if (detectedName == "Rainbow") {
      Serial.println("Rainbow beacon: spinning rainbow");
      strip.setBrightness(MAX_BRIGHTNESS);
      rainbowActive = true;
      rainbowOffset = 0;
    } else if (detectedName == "Dying Flicker") {
      Serial.println("Dying Flicker beacon: nearly dead bulb");
      flickerActive = true;
      flickerBrightness = 128;
      flickerDimming = true;
    } else if (detectedName == "Red Dying Flicker") {
      Serial.println("Red Dying Flicker beacon: nearly dead red bulb");
      flickerActive = true;
      flickerBrightness = 128;
      flickerDimming = true;
      setAll(255, 0, 0);
    } else {
      Serial.println("No beacon: warm white");
      strip.setBrightness(MAX_BRIGHTNESS);
      warmWhite();
    }

    updateMuseumStatus();
  }

  detectedName = "";

  BLEDevice::getScan()->clearResults();
  startBLEScan();
}

void startBLEScan() {
  BLEDevice::getScan()->start(SCAN_TIME, scanComplete, false);
}

// ==================================================
// SETUP / LOOP
// ==================================================

void setup() {
  Serial.begin(115200);
  Serial.println("Museum Flashlight Starting...");

  strip.begin();
  strip.setBrightness(MAX_BRIGHTNESS);
  warmWhite();

  BLEDevice::init(FLASHLIGHT_ID);

  // Added identity/session API.
  setupMuseumBLEIdentity();

  // Original room beacon scanning.
  BLEScan* pBLEScan = BLEDevice::getScan();
  pBLEScan->setAdvertisedDeviceCallbacks(new ScanCallback());
  pBLEScan->setActiveScan(true);

  startBLEScan();
}

void loop() {
  if (flickerActive) {
    updateFlicker();
  }

  if (rainbowActive) {
    updateRainbow();
  }

  // Periodic status update.
  static unsigned long lastStatusUpdate = 0;
  if (millis() - lastStatusUpdate >= 5000) {
    lastStatusUpdate = millis();
    updateMuseumStatus();
  }

  delay(10);
}
